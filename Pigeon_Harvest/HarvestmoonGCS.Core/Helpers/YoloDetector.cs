using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Helpers
{
    /// <summary>
    /// YOLOv8n-compatible object detector using ONNX Runtime with optional CUDA support.
    /// </summary>
    public class YoloDetector : IDisposable
    {
        private InferenceSession _session;
        private string[] _classNames;
        private string _inputName = "images";
        private int _inputWidth = 416;
        private int _inputHeight = 416;
        private float _confThreshold = 0.25f; // Lowered from 0.35 so more detections surface on the dashboard overlay
        private float _nmsThreshold = 0.5f;
        private bool _isInitialized = false;

        public bool IsInitialized => _isInitialized;
        public string[] ClassNames => _classNames;
        public InferenceSession Session => _session;
        public string InputName => _inputName;
        public int InputWidth => _inputWidth;
        public int InputHeight => _inputHeight;

        /// <summary>
        /// Initialize YOLO detector with model and class names
        /// </summary>
        /// <param name="modelPath">Path to .onnx model file</param>
        /// <param name="classNamesPath">Path to class names file</param>
        /// <param name="useCuda">Use CUDA GPU acceleration</param>
        public bool Initialize(string modelPath, string classNamesPath, bool useCuda = true)
        {
            try
            {
                if (!File.Exists(modelPath))
                {
                    Serilog.Log.Error("[YoloDetector] Model file not found: {Path}", modelPath);
                    return false;
                }

                if (!File.Exists(classNamesPath))
                {
                    Serilog.Log.Error("[YoloDetector] Class names file not found: {Path}", classNamesPath);
                    return false;
                }

                // Load class names
                _classNames = File.ReadAllLines(classNamesPath)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();

                Serilog.Log.Information("[YoloDetector] Loaded {Count} class names", _classNames.Length);

                // Create session options with multi-threading
                var sessionOptions = new SessionOptions();
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
#if __ANDROID__
                // Low-power Android tablets perform better when ONNX does not occupy
                // every core and fight the camera/UI threads. Four worker threads is a
                // good ceiling for Realme Pad Mini class SoCs.
                sessionOptions.IntraOpNumThreads = Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2));
                sessionOptions.InterOpNumThreads = 1;
                sessionOptions.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                sessionOptions.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
#else
                sessionOptions.IntraOpNumThreads = Environment.ProcessorCount; // Use all CPU cores
                sessionOptions.InterOpNumThreads = Environment.ProcessorCount;
#endif

#if __ANDROID__
                try
                {
                    // INT8 model: use CPU fallback flag so NNAPI runs INT8 ops natively.
                    // NNAPI_FLAG_CPU_ONLY is omitted intentionally — we want NPU/DSP if available.
                    sessionOptions.AppendExecutionProvider_Nnapi(
                        NnapiFlags.NNAPI_FLAG_CPU_DISABLED | NnapiFlags.NNAPI_FLAG_USE_FP16);
                    System.Diagnostics.Debug.WriteLine("[YoloDetector] NNAPI execution provider enabled (INT8 + FP16 fallback)");
                }
                catch (Exception nnapiEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[YoloDetector] NNAPI unavailable: {nnapiEx.Message}. Using CPU execution provider.");
                }
#endif

                // Try CUDA (GPU) first, but only when the user explicitly opts in via the
                // PIGEON_YOLO_USE_CUDA environment variable. AppendExecutionProvider_CUDA on a
                // CPU-only Linux box throws and sometimes leaves the session options in a state
                // that prevents InferenceSession from loading at all, which is why the dashboard
                // was falling back to OpenCV ("Yolo Fallback" pill).
                bool cudaRequested = useCuda && string.Equals(
                    Environment.GetEnvironmentVariable("PIGEON_YOLO_USE_CUDA"), "1", StringComparison.Ordinal);
                if (cudaRequested)
                {
                    try
                    {
                        sessionOptions.AppendExecutionProvider_CUDA(0);
                        System.Diagnostics.Debug.WriteLine("[YoloDetector] CUDA execution provider enabled - using GPU");
                    }
                    catch (Exception cudaEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[YoloDetector] CUDA requested but unavailable: {cudaEx.Message}. Falling back to CPU.");
                        // Recreate options to discard any partial CUDA state that might block CPU execution.
                        sessionOptions.Dispose();
                        sessionOptions = new SessionOptions
                        {
                            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                            IntraOpNumThreads = Environment.ProcessorCount,
                            InterOpNumThreads = Environment.ProcessorCount
                        };
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[YoloDetector] Using CPU execution provider (default)");
                }

                // Create inference session
                Serilog.Log.Information("[YoloDetector] Creating InferenceSession for: {Path}", modelPath);
                _session = new InferenceSession(modelPath, sessionOptions);
                ConfigureInputSizeFromModel();
                _isInitialized = true;

                Serilog.Log.Information("[YoloDetector] Initialized successfully with {Count} classes, input {Width}x{Height}",
                    _classNames.Length,
                    _inputWidth,
                    _inputHeight);
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[YoloDetector] Initialization failed");
                return false;
            }
        }

        private void ConfigureInputSizeFromModel()
        {
            try
            {
                var input = _session.InputMetadata.FirstOrDefault();
                var dimensions = input.Value.Dimensions;
                if (dimensions.Length >= 4 && dimensions[2] > 0 && dimensions[3] > 0)
                {
                    _inputHeight = dimensions[2];
                    _inputWidth = dimensions[3];
                }
                if (!string.IsNullOrWhiteSpace(input.Key))
                {
                    _inputName = input.Key;
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[YoloDetector] Could not read model input size; using default {Width}x{Height}",
                    _inputWidth,
                    _inputHeight);
            }
        }

        /// <summary>
        /// Set confidence threshold
        /// </summary>
        public void SetConfidenceThreshold(float threshold)
        {
            _confThreshold = Math.Max(0.1f, Math.Min(1.0f, threshold));
        }

        /// <summary>
        /// Set non-maximum suppression threshold
        /// </summary>
        public void SetNmsThreshold(float threshold)
        {
            _nmsThreshold = Math.Max(0.1f, Math.Min(1.0f, threshold));
        }

        /// <summary>
        /// Detect objects in frame
        /// </summary>
        public List<DetectionResult> Detect(Mat frame)
        {
            var results = new List<DetectionResult>();

            if (!_isInitialized || frame == null || frame.Empty())
                return results;

            try
            {
                // Preprocess image
                var inputTensor = PreprocessImage(frame);

                // Run inference
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
                };

                using (var outputs = _session.Run(inputs))
                {
                    // Process outputs
                    results = PostProcess(outputs.Select(output => output.AsTensor<float>()).ToList(), frame.Width, frame.Height);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[YoloDetector] Detection error: {ex.Message}");
            }

            return results;
        }

        // Maps internal model class names → display labels.
        // The bundled moonharvest-uav-det.onnx emits exactly these four classes (verified from
        // the ONNX metadata: Lush Green / Inconsistent Growth / Drought / Severe Stress /
        // Bare Soil / Gap). The legacy v3 agri names are kept as aliases so older models still
        // render sensibly.
        private static readonly Dictionary<string, string> _classDisplayMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // 4-class MoonHarvest detector (current)
            { "Lush Green",              "Lush Green"          },
            { "Inconsistent Growth",     "Inconsistent Growth" },
            { "Drought / Severe Stress", "Drought / Severe Stress" },
            { "Bare Soil / Gap",         "Bare Soil / Gap"     },
            // snake_case variants produced by the classifier / older exports
            { "lush_green",              "Lush Green"          },
            { "inconsistent_growth",     "Inconsistent Growth" },
            { "drought_severe_stress",   "Drought / Severe Stress" },
            { "bare_soil",               "Bare Soil / Gap"     },
            { "bare_soil_gap",           "Bare Soil / Gap"     },
            // Legacy v3 agri classes
            { "well_irrigated",          "Well Irrigated"      },
            { "soil_issues",             "Soil Issues"         },
            { "disease",                 "Disease"             },
            { "pest",                    "Pest"                },
        };

        private static readonly Dictionary<string, Scalar> _demoColorMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Lush Green",              new Scalar(50,  205,  50)  },  // green  (BGR)
            { "Inconsistent Growth",     new Scalar(0,   200, 255)  },  // yellow
            { "Drought / Severe Stress", new Scalar(0,    90, 255)  },  // orange-red
            { "Bare Soil / Gap",         new Scalar(140, 140, 140)  },  // grey/brown
            { "Well Irrigated",          new Scalar(200, 150,   2)  },  // teal (legacy)
            { "Soil Issues",             new Scalar(55,   64,  93)  },  // brown (legacy)
            { "Disease",                 new Scalar(0,    60, 255)  },  // red (legacy)
            { "Pest",                    new Scalar(0,   140, 255)  },  // orange (legacy)
        };

        /// <summary>
        /// Draw detection results on frame
        /// </summary>
        public Mat DrawDetections(Mat frame, List<DetectionResult> detections)
        {
            // Clone frame to prevent issues with frame reuse
            var output = frame.Clone();

            foreach (var det in detections)
            {
                // Remap internal class name to demo display label; skip hidden classes (null)
                if (_classDisplayMap.TryGetValue(det.ClassName, out var displayLabel) && displayLabel == null)
                    continue;
                var labelName = displayLabel ?? det.ClassName;

                var color = _demoColorMap.TryGetValue(labelName, out var dc) ? dc : GetClassColor(det.ClassId);

                // Draw bounding box
                Cv2.Rectangle(output, det.BoundingBox, color, 2);

                // Draw label background
                string label = $"{labelName} {det.Confidence:P0}";
                var textSize = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.6, 2, out int baseline);
                var labelRect = new Rect(
                    det.BoundingBox.X,
                    det.BoundingBox.Y - textSize.Height - 10,
                    textSize.Width + 10,
                    textSize.Height + 10
                );
                Cv2.Rectangle(output, labelRect, color, -1);

                // Draw label text
                Cv2.PutText(output, label,
                    new Point(det.BoundingBox.X + 5, det.BoundingBox.Y - 5),
                    HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);
            }

            return output;
        }

        private DenseTensor<float> PreprocessImage(Mat frame)
        {
            // Resize to input size
            var resized = new Mat();
            Cv2.Resize(frame, resized, new Size(_inputWidth, _inputHeight));

            // Convert BGR to RGB
            var rgb = new Mat();
            Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

            // Create tensor
            var tensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });

            // OPTIMIZED: Use GetArray for direct memory access (10-20x faster than At<T>)
            rgb.GetArray(out Vec3b[] pixels);
            
            // Use parallel processing for faster conversion
            int width = _inputWidth;
            System.Threading.Tasks.Parallel.For(0, _inputHeight, y =>
            {
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    var pixel = pixels[rowStart + x];
                    tensor[0, 0, y, x] = pixel.Item0 * 0.00392156862f; // /255.0f optimized
                    tensor[0, 1, y, x] = pixel.Item1 * 0.00392156862f;
                    tensor[0, 2, y, x] = pixel.Item2 * 0.00392156862f;
                }
            });

            resized.Dispose();
            rgb.Dispose();

            return tensor;
        }

        /// <summary>
        /// Public wrapper for PostProcess so external callers (e.g. SkiaSharp-based pipeline)
        /// can run post-processing on raw ONNX output without going through the OpenCvSharp Detect path.
        /// </summary>
        public List<DetectionResult> PostProcessPublic(IReadOnlyList<Tensor<float>> outputs, int originalWidth, int originalHeight)
            => PostProcess(outputs, originalWidth, originalHeight);

        private List<DetectionResult> PostProcess(IReadOnlyList<Tensor<float>> outputs, int originalWidth, int originalHeight)
        {
            if (outputs.Count >= 2 && outputs.Any(IsYoloV4TinyOutput))
            {
                return PostProcessYoloV4Tiny(outputs, originalWidth, originalHeight);
            }

            return outputs.Count > 0
                ? PostProcessYoloV8(outputs[0], originalWidth, originalHeight)
                : new List<DetectionResult>();
        }

        private static bool IsYoloV4TinyOutput(Tensor<float> output)
            => output.Dimensions.Length == 4 && output.Dimensions[1] % 85 == 0;

        private List<DetectionResult> PostProcessYoloV8(Tensor<float> output, int originalWidth, int originalHeight)
        {
            var results = new List<DetectionResult>();
            
            // YOLOv8n output shape: [1, 84, 8400] for COCO (84 = 4 bbox + 80 classes).
            int numClasses = _classNames.Length;
            int numDetections = output.Dimensions[2];

            float xScale = (float)originalWidth / _inputWidth;
            float yScale = (float)originalHeight / _inputHeight;

            // OPTIMIZED: Convert tensor to array for faster access
            float[] outputArray = output.ToArray();
            int stride = numDetections; // Each row has numDetections elements

            // Pre-filter candidates in parallel
            var candidates = new System.Collections.Concurrent.ConcurrentBag<(int idx, float score, int classId)>();
            
            System.Threading.Tasks.Parallel.For(0, numDetections, i =>
            {
                // Quick check: find max class score
                float maxScore = 0;
                int maxClassId = 0;

                int baseIdx = (4 * stride) + i; // Start at class scores
                for (int c = 0; c < numClasses; c++)
                {
                    float score = outputArray[baseIdx + (c * stride)];
                    if (score > maxScore)
                    {
                        maxScore = score;
                        maxClassId = c;
                    }
                }

                if (maxScore > _confThreshold)
                {
                    candidates.Add((i, maxScore, maxClassId));
                }
            });

            // Extract bounding boxes only for candidates
            var boxes = new List<Rect>();
            var confidences = new List<float>();
            var classIds = new List<int>();

            foreach (var (i, maxScore, maxClassId) in candidates)
            {
                // Get bounding box (center x, center y, width, height)
                float cx = outputArray[i];
                float cy = outputArray[stride + i];
                float w = outputArray[(2 * stride) + i];
                float h = outputArray[(3 * stride) + i];

                // Convert to top-left corner
                int x = (int)((cx - w / 2) * xScale);
                int y = (int)((cy - h / 2) * yScale);
                int boxWidth = (int)(w * xScale);
                int boxHeight = (int)(h * yScale);

                // Clamp to image bounds
                x = Math.Max(0, Math.Min(x, originalWidth - 1));
                y = Math.Max(0, Math.Min(y, originalHeight - 1));
                boxWidth = Math.Min(boxWidth, originalWidth - x);
                boxHeight = Math.Min(boxHeight, originalHeight - y);

                if (boxWidth > 0 && boxHeight > 0)
                {
                    boxes.Add(new Rect(x, y, boxWidth, boxHeight));
                    confidences.Add(maxScore);
                    classIds.Add(maxClassId);
                }
            }

            // Apply Non-Maximum Suppression
            if (boxes.Count > 0)
            {
                var selectedIndices = ApplyNMS(boxes, confidences, _nmsThreshold);

                foreach (int idx in selectedIndices.Take(20))
                {
                    results.Add(new DetectionResult
                    {
                        ClassId = classIds[idx],
                        ClassName = classIds[idx] < _classNames.Length ? _classNames[classIds[idx]] : "unknown",
                        Confidence = confidences[idx],
                        BoundingBox = boxes[idx]
                    });
                }
            }

            return results;
        }

        private List<DetectionResult> PostProcessYoloV4Tiny(IReadOnlyList<Tensor<float>> outputs, int originalWidth, int originalHeight)
        {
            var boxes = new List<Rect>();
            var confidences = new List<float>();
            var classIds = new List<int>();

            foreach (var output in outputs.OrderBy(o => o.Dimensions[2]))
            {
                var dims = output.Dimensions;
                if (dims.Length != 4 || dims[1] % 85 != 0)
                {
                    continue;
                }

                var gridH = dims[2];
                var gridW = dims[3];
                var values = output.ToArray();
                var anchors = gridW <= 13
                    ? new (float W, float H)[] { (81, 82), (135, 169), (344, 319) }
                    : new (float W, float H)[] { (10, 14), (23, 27), (37, 58) };

                for (var anchorIndex = 0; anchorIndex < anchors.Length; anchorIndex++)
                {
                    var channelOffset = anchorIndex * 85;
                    for (var y = 0; y < gridH; y++)
                    {
                        for (var x = 0; x < gridW; x++)
                        {
                            var objectness = Sigmoid(ReadNchw(values, dims, channelOffset + 4, y, x));
                            if (objectness < 0.01f)
                            {
                                continue;
                            }

                            var bestClassScore = 0f;
                            var bestClassId = 0;
                            for (var c = 0; c < Math.Min(_classNames.Length, 80); c++)
                            {
                                var classScore = Sigmoid(ReadNchw(values, dims, channelOffset + 5 + c, y, x));
                                if (classScore > bestClassScore)
                                {
                                    bestClassScore = classScore;
                                    bestClassId = c;
                                }
                            }

                            var confidence = objectness * bestClassScore;
                            if (confidence < _confThreshold)
                            {
                                continue;
                            }

                            var cx = (Sigmoid(ReadNchw(values, dims, channelOffset, y, x)) + x) / gridW;
                            var cy = (Sigmoid(ReadNchw(values, dims, channelOffset + 1, y, x)) + y) / gridH;
                            var bw = MathF.Exp(Clamp(ReadNchw(values, dims, channelOffset + 2, y, x), -10f, 10f)) * anchors[anchorIndex].W / _inputWidth;
                            var bh = MathF.Exp(Clamp(ReadNchw(values, dims, channelOffset + 3, y, x), -10f, 10f)) * anchors[anchorIndex].H / _inputHeight;

                            var boxWidth = (int)(bw * originalWidth);
                            var boxHeight = (int)(bh * originalHeight);
                            var boxX = (int)((cx * originalWidth) - boxWidth / 2f);
                            var boxY = (int)((cy * originalHeight) - boxHeight / 2f);

                            boxX = Math.Max(0, Math.Min(boxX, originalWidth - 1));
                            boxY = Math.Max(0, Math.Min(boxY, originalHeight - 1));
                            boxWidth = Math.Min(boxWidth, originalWidth - boxX);
                            boxHeight = Math.Min(boxHeight, originalHeight - boxY);

                            if (boxWidth > 0 && boxHeight > 0)
                            {
                                boxes.Add(new Rect(boxX, boxY, boxWidth, boxHeight));
                                confidences.Add(confidence);
                                classIds.Add(bestClassId);
                            }
                        }
                    }
                }
            }

            var results = new List<DetectionResult>();
            foreach (var idx in ApplyNMS(boxes, confidences, _nmsThreshold).Take(50))
            {
                results.Add(new DetectionResult
                {
                    ClassId = classIds[idx],
                    ClassName = classIds[idx] < _classNames.Length ? _classNames[classIds[idx]] : "unknown",
                    Confidence = confidences[idx],
                    BoundingBox = boxes[idx]
                });
            }

            return results;
        }

        private static float ReadNchw(float[] values, ReadOnlySpan<int> dims, int channel, int y, int x)
        {
            var height = dims[2];
            var width = dims[3];
            return values[((channel * height) + y) * width + x];
        }

        private static float Sigmoid(float value)
            => 1f / (1f + MathF.Exp(-Clamp(value, -80f, 80f)));

        private static float Clamp(float value, float min, float max)
            => MathF.Max(min, MathF.Min(max, value));

        private List<int> ApplyNMS(List<Rect> boxes, List<float> confidences, float nmsThreshold)
        {
            var indices = new List<int>();
            var sortedIndices = confidences
                .Select((conf, idx) => new { Confidence = conf, Index = idx })
                .OrderByDescending(x => x.Confidence)
                .Select(x => x.Index)
                .ToList();

            var suppressed = new bool[boxes.Count];

            foreach (int i in sortedIndices)
            {
                if (suppressed[i]) continue;

                indices.Add(i);
                var boxA = boxes[i];

                for (int j = 0; j < boxes.Count; j++)
                {
                    if (i == j || suppressed[j]) continue;

                    var boxB = boxes[j];
                    float iou = ComputeIoU(boxA, boxB);

                    if (iou > nmsThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }

            return indices;
        }

        private float ComputeIoU(Rect a, Rect b)
        {
            int x1 = Math.Max(a.X, b.X);
            int y1 = Math.Max(a.Y, b.Y);
            int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            int intersectionArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            int areaA = a.Width * a.Height;
            int areaB = b.Width * b.Height;
            int unionArea = areaA + areaB - intersectionArea;

            return unionArea > 0 ? (float)intersectionArea / unionArea : 0;
        }

        private Scalar GetClassColor(int classId)
        {
            // Generate consistent color for each class
            var colors = new Scalar[]
            {
                new Scalar(0, 255, 0),    // Green
                new Scalar(255, 0, 0),    // Blue
                new Scalar(0, 0, 255),    // Red
                new Scalar(255, 255, 0),  // Cyan
                new Scalar(255, 0, 255),  // Magenta
                new Scalar(0, 255, 255),  // Yellow
                new Scalar(128, 255, 0),  // Lime
                new Scalar(255, 128, 0),  // Orange
                new Scalar(128, 0, 255),  // Purple
                new Scalar(0, 128, 255),  // Light Blue
            };
            return colors[classId % colors.Length];
        }

        public void Dispose()
        {
            _session?.Dispose();
            _isInitialized = false;
        }
    }
}
