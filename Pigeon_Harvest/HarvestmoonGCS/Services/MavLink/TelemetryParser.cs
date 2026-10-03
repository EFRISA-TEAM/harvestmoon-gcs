using System;
using MavLinkNet;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Models;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Parses incoming MAVLink packets and extracts telemetry data
/// </summary>
internal class TelemetryParser
{
    private readonly MavLinkService _service;
    private FlightData _currentData = new FlightData();
    private readonly object _dataLock = new object();
    
    public TelemetryParser(MavLinkService service)
    {
        _service = service;
    }
    
    public void ParsePacket(UasMessage message)
    {
        try
        {
            // Check message type by MessageId
            switch (message.MessageId)
            {
                case 0: // HEARTBEAT
                    if (message is UasHeartbeat heartbeat)
                        ParseHeartbeat(heartbeat);
                    break;
                    
                case 30: // ATTITUDE
                    if (message is UasAttitude attitude)
                        ParseAttitude(attitude);
                    break;
                    
                case 33: // GLOBAL_POSITION_INT
                    if (message is UasGlobalPositionInt position)
                        ParseGlobalPositionInt(position);
                    break;
                    
                case 74: // VFR_HUD
                    if (message is UasVfrHud hud)
                        ParseVfrHud(hud);
                    break;
                    
                case 1: // SYS_STATUS
                    if (message is UasSysStatus status)
                        ParseSysStatus(status);
                    break;
                    
                case 24: // GPS_RAW_INT
                    if (message is UasGpsRawInt gps)
                        ParseGpsRawInt(gps);
                    break;
                    
                case 253: // STATUSTEXT
                    if (message is UasStatustext statusText)
                        ParseStatusText(statusText);
                    break;
                    
                default:
                    // Ignore other message types
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TelemetryParser] Error parsing packet: {ex.Message}");
            Console.WriteLine($"[TelemetryParser] Error parsing packet: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    public FlightData GetCurrentData()
    {
        lock (_dataLock)
        {
            return _currentData;
        }
    }
    
    private void ParseHeartbeat(UasHeartbeat heartbeat)
    {
        lock (_dataLock)
        {
            // Update flight mode
            _currentData.FlightMode = MapFlightMode(heartbeat.CustomMode);
            
            // H2: IsArmed dari MAV_MODE_FLAG_SAFETY_ARMED bit di base_mode
            _currentData.IsArmed = ((byte)heartbeat.BaseMode & 0x80) != 0;

            // Extract vehicle type from HEARTBEAT (MAV_TYPE)
            int vehicleType = (int)heartbeat.Type;
            if (vehicleType == 0) vehicleType = 1;
            _currentData.Type = vehicleType;
            
            System.Diagnostics.Debug.WriteLine($"[TelemetryParser] Heartbeat: Mode={_currentData.FlightMode}, Armed={_currentData.IsArmed}, VehicleType={vehicleType}");
        }
        
        // Raise telemetry event
        _service.RaiseTelemetryReceived(_currentData);
    }
    
    private void ParseAttitude(UasAttitude attitude)
    {
        lock (_dataLock)
        {
            // Convert radians to degrees
            var rollDeg = (float)(attitude.Roll * 180.0 / Math.PI);
            var pitchDeg = (float)(attitude.Pitch * 180.0 / Math.PI);
            var yawDeg = (float)(attitude.Yaw * 180.0 / Math.PI);
            
            // Normalize yaw to 0-360
            if (yawDeg < 0)
                yawDeg += 360;
            
            // Update data
            _currentData.IMU.Roll = rollDeg;
            _currentData.IMU.Pitch = pitchDeg;
            _currentData.IMU.Yaw = yawDeg;

            System.Diagnostics.Debug.WriteLine($"[TelemetryParser] Attitude: Roll={rollDeg:F1}°, Pitch={pitchDeg:F1}°, Yaw={yawDeg:F1}°");
        }

        // Raise telemetry event
        _service.RaiseTelemetryReceived(_currentData);
    }
    
    private void ParseGlobalPositionInt(UasGlobalPositionInt position)
    {
        lock (_dataLock)
        {
            // GPS coordinates (already in 1E7 format)
            _currentData.GPS.Latitude = position.Lat;
            _currentData.GPS.Longitude = position.Lon;
            
            // Altitude (convert from mm to m)
            _currentData.AltitudeFloat = position.Alt / 1000.0f;
            _currentData.Barometers = position.RelativeAlt / 1000.0f;
            
            // Heading (convert from centidegrees to degrees)
            _currentData.IMU.Yaw = position.Hdg / 100.0f;
            
            System.Diagnostics.Debug.WriteLine($"[TelemetryParser] Position: Lat={_currentData.GPS.Latitude / 1e7:F6}, Lon={_currentData.GPS.Longitude / 1e7:F6}, Alt={_currentData.AltitudeFloat:F1}m");
        }
        
        // Raise telemetry event
        _service.RaiseTelemetryReceived(_currentData);
    }
    
    private void ParseVfrHud(UasVfrHud hud)
    {
        lock (_dataLock)
        {
            // H4: AirSpeed dari VFR_HUD.airspeed, GroundSpeed dari VFR_HUD.groundspeed — berbeda!
            _currentData.Speed = hud.Airspeed;
            _currentData.GroundSpeed = hud.Groundspeed;
            
            // Altitude (m)
            _currentData.AltitudeFloat = hud.Alt;
            
            // H4: VerticalSpeed dari VFR_HUD.climb (m/s naik positif)
            _currentData.VerticalSpeed = hud.Climb;
            
            // Throttle (0-100%)
            _currentData.ThrottlePercent = (int)hud.Throttle;
            
            System.Diagnostics.Debug.WriteLine($"[TelemetryParser] VFR_HUD: Air={_currentData.Speed:F1}m/s, Ground={_currentData.GroundSpeed:F1}m/s, Alt={_currentData.AltitudeFloat:F1}m, Climb={_currentData.VerticalSpeed:F1}m/s, Throttle={_currentData.ThrottlePercent}%");
        }
        
        // Raise telemetry event
        _service.RaiseTelemetryReceived(_currentData);
    }
    
    private void ParseSysStatus(UasSysStatus status)
    {
        lock (_dataLock)
        {
            // Battery voltage (mV)
            _currentData.BatteryVolt = status.VoltageBattery;
            
            // Battery current (cA → dA)
            _currentData.BatteryCurr = (ushort)(status.CurrentBattery / 10);
            
            // H2: Battery remaining percentage (-1 = unknown)
            _currentData.BatteryRemaining = status.BatteryRemaining;
            
            System.Diagnostics.Debug.WriteLine($"[TelemetryParser] SYS_STATUS: Voltage={_currentData.BatteryVolt}mV, Current={_currentData.BatteryCurr}, Remaining={_currentData.BatteryRemaining}%");
        }
        
        // Raise telemetry event
        _service.RaiseTelemetryReceived(_currentData);
    }
    
    private void ParseGpsRawInt(UasGpsRawInt gps)
    {
        lock (_dataLock)
        {
            // Satellite count
            _currentData.Sats = gps.SatellitesVisible;
            
            // HDOP (horizontal dilution of precision)
            _currentData.Hdop = gps.Eph;
            
            System.Diagnostics.Debug.WriteLine($"[TelemetryParser] GPS_RAW: Sats={_currentData.Sats}, HDOP={_currentData.Hdop}");
        }
        
        // Raise telemetry event
        _service.RaiseTelemetryReceived(_currentData);
    }
    
    private void ParseStatusText(UasStatustext statusText)
    {
        try
        {
            // Convert char array to string
            string message = new string(statusText.Text).TrimEnd('\0');
            
            System.Diagnostics.Debug.WriteLine($"[TelemetryParser] STATUS_TEXT: {message}");
            
            // Raise message received event
            _service.RaiseMessageReceived(message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TelemetryParser] Error parsing status text: {ex.Message}");
        }
    }
    
    private FlightMode MapFlightMode(uint customMode)
    {
        // Map ArduPilot Copter custom mode numbers → FlightMode enum
        // L-6 fix: tambah mode 8 (AUTO_TUNE), 11 (DRIFT), 13 (SPORT),
        //          15 (BRAKE), 17 (AVOID), 19 (POSHOLD), dll.
        return customMode switch
        {
            0  => FlightMode.MANUAL,     // STABILIZE
            1  => FlightMode.MANUAL,     // ACRO
            2  => FlightMode.HOLD_ALTITUDE, // ALT_HOLD
            3  => FlightMode.AUTO,       // AUTO
            4  => FlightMode.AUTO,       // GUIDED
            5  => FlightMode.LOITER,     // LOITER
            6  => FlightMode.RTL,        // RTL
            7  => FlightMode.LOITER,     // CIRCLE
            8  => FlightMode.AUTO,       // AUTO_TUNE → AUTO (closest)
            9  => FlightMode.LAND,       // LAND
            10 => FlightMode.RTL,        // OF_LOITER → RTL
            11 => FlightMode.MANUAL,     // DRIFT
            13 => FlightMode.MANUAL,     // SPORT
            14 => FlightMode.MANUAL,     // FLIP
            15 => FlightMode.MANUAL,     // AUTO_TUNE (duplicate in some firmwares)
            16 => FlightMode.LOITER,     // POSHOLD
            17 => FlightMode.HOLD_ALTITUDE, // BRAKE
            18 => FlightMode.MANUAL,     // THROW
            19 => FlightMode.AUTO,       // AVOID_ADSB
            20 => FlightMode.AUTO,       // GUIDED_NOGPS
            21 => FlightMode.RTL,        // SMART_RTL
            _  => FlightMode.MANUAL
        };
    }
}
