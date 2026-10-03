using System;
using System.Threading.Tasks;
using MavLinkNet;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Diagnostics;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Sends commands to the drone
/// </summary>
internal class CommandSender
{
    private readonly MavLinkService _service;
    
    public CommandSender(MavLinkService service)
    {
        _service = service;
    }
    
    public async Task<bool> SendArmDisarmAsync(bool arm)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.ComponentArmDisarm,
                arm ? 1.0f : 0.0f,  // param1: 1=arm, 0=disarm
                0, 0, 0, 0, 0, 0
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Arm/Disarm failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendSetModeAsync(string mode)
    {
        try
        {
            // Map mode name to mode number (simplified - ArduCopter modes)
            uint customMode = mode.ToUpper() switch
            {
                "STABILIZE" => 0,
                "ACRO" => 1,
                "ALT_HOLD" => 2,
                "AUTO" => 3,
                "GUIDED" => 4,
                "LOITER" => 5,
                "RTL" => 6,
                "CIRCLE" => 7,
                "LAND" => 9,
                "DRIFT" => 11,
                "SPORT" => 13,
                "FLIP" => 14,
                "AUTOTUNE" => 15,
                "POSHOLD" => 16,
                "BRAKE" => 17,
                _ => 0
            };
            
            var command = CreateCommandLong(
                (int)MavCmd.DoSetMode,
                (float)MavModeFlagDecodePosition.CustomMode,  // param1: mode flag
                customMode,  // param2: custom mode
                0, 0, 0, 0, 0
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Set mode failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task SendCommandLongAsync(int command, float param1, float param2,
                                            float param3, float param4, float param5,
                                            float param6, float param7)
    {
        try
        {
            var cmd = CreateCommandLong(command, param1, param2, param3, param4, param5, param6, param7);
            SendMessage(cmd);
            
            _service.GetDiagnosticLogger().LogCommand($"CMD_{command}", 
                $"p1={param1}, p2={param2}, p3={param3}, p4={param4}, p5={param5}, p6={param6}, p7={param7}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Command failed: {ex.Message}");
        }
        
        await Task.CompletedTask;
    }
    
    public void SendMessage(UasMessage message)
    {
        if (_service.IsInPlaybackMode)
        {
            System.Diagnostics.Debug.WriteLine("[CommandSender] Blocked send in playback mode");
            return;
        }
        
        var transport = _service.GetTransport();
        if (transport == null)
        {
            throw new InvalidOperationException("Not connected");
        }
        
        transport.SendMessage(message);
    }
    
    // VTOL Operations
    public async Task<bool> SendVtolTransitionAsync(MavLinkNet.MavVtolState targetState)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.DoVtolTransition,
                (float)targetState,  // param1: target state
                0, 0, 0, 0, 0, 0
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] VTOL transition failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendVtolTakeoffAsync(double latitude, double longitude, double altitude, float heading)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.NavVtolTakeoff,
                0,  // param1: empty
                0,  // param2: transition heading
                0,  // param3: empty
                heading,  // param4: yaw angle
                (float)latitude,  // param5: latitude
                (float)longitude,  // param6: longitude
                (float)altitude  // param7: altitude
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] VTOL takeoff failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendVtolLandAsync(double latitude, double longitude, double altitude)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.NavVtolLand,
                0,  // param1: empty
                0,  // param2: empty
                0,  // param3: approach altitude
                0,  // param4: yaw angle
                (float)latitude,  // param5: latitude
                (float)longitude,  // param6: longitude
                (float)altitude  // param7: altitude
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] VTOL land failed: {ex.Message}");
            return false;
        }
    }
    
    // Mission Execution
    public async Task<bool> SendStartMissionAsync(int firstItem = 0, int lastItem = 0)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.MissionStart,
                firstItem,  // param1: first item
                lastItem,   // param2: last item
                0, 0, 0, 0, 0
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Start mission failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendPauseMissionAsync()
    {
        try
        {
            // Pause by switching to LOITER mode
            return await SendSetModeAsync("LOITER");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Pause mission failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendResumeMissionAsync()
    {
        try
        {
            // Resume by switching back to AUTO mode
            return await SendSetModeAsync("AUTO");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Resume mission failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendSetCurrentWaypointAsync(int waypointIndex)
    {
        try
        {
            var msg = new UasMissionSetCurrent
            {
                TargetSystem = _service.GetTargetSystemId(),
                TargetComponent = _service.GetTargetComponentId(),
                Seq = (ushort)waypointIndex
            };
            
            SendMessage(msg);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Set current waypoint failed: {ex.Message}");
            return false;
        }
    }
    
    // Payload/Gripper Operations
    public async Task<bool> SendGripperActionAsync(int action, int gripperNum = 0)
    {
        try
        {
            var command = CreateCommandLong(
                2000, // MAV_CMD_DO_GRIPPER (not in enum, use raw value)
                gripperNum,  // param1: gripper number
                action,      // param2: action (0=release, 1=grab)
                0, 0, 0, 0, 0
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Gripper action failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendDeployPayloadAsync()
    {
        try
        {
            // Deploy using gripper release
            return await SendGripperActionAsync(0, 0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Deploy payload failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendReleasePayloadAsync()
    {
        try
        {
            // Release using servo or relay
            return await SendSetServoAsync(9, 1900);  // Servo 9, high PWM
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Release payload failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendParachuteActionAsync(ParachuteAction action)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.DoParachute,
                (float)action,  // param1: action
                0, 0, 0, 0, 0, 0
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Parachute action failed: {ex.Message}");
            return false;
        }
    }
    
    // Servo Control
    public async Task<bool> SendSetServoAsync(int servoNum, int pwmValue)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.DoSetServo,
                servoNum,   // param1: servo number
                pwmValue,   // param2: PWM value
                0, 0, 0, 0, 0
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Set servo failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendSetRelayAsync(int relayNum, bool state)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.DoSetRelay,
                relayNum,        // param1: relay number
                state ? 1 : 0,   // param2: state (0=off, 1=on)
                0, 0, 0, 0, 0
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Set relay failed: {ex.Message}");
            return false;
        }
    }
    
    // Speed Control
    public async Task<bool> SendChangeSpeedAsync(float speedType, float speed, float throttle = -1)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.DoChangeSpeed,
                speedType,  // param1: speed type (0=airspeed, 1=groundspeed)
                speed,      // param2: speed in m/s
                throttle,   // param3: throttle percentage (-1 = no change)
                0, 0, 0, 0
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Change speed failed: {ex.Message}");
            return false;
        }
    }
    
    // Takeoff/Land Commands
    public async Task<bool> SendTakeoffAsync(float altitude)
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.NavTakeoff,
                0,  // param1: pitch
                0,  // param2: empty
                0,  // param3: empty
                0,  // param4: yaw angle
                0,  // param5: latitude (0 = current)
                0,  // param6: longitude (0 = current)
                altitude  // param7: altitude
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Takeoff failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendLandAsync()
    {
        try
        {
            var command = CreateCommandLong(
                (int)MavCmd.NavLand,
                0,  // param1: abort altitude
                0,  // param2: land mode
                0,  // param3: empty
                0,  // param4: yaw angle
                0,  // param5: latitude (0 = current)
                0,  // param6: longitude (0 = current)
                0   // param7: altitude
            );
            
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Land failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> SendRTLAsync()
    {
        try
        {
            return await SendSetModeAsync("RTL");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] RTL failed: {ex.Message}");
            return false;
        }
    }

    // Real Flight Motor Test Operations
    public async Task<bool> SendMotorTestAsync(int motorInstance, float throttle, float timeoutSeconds, int motorCount = 1, int throttleType = 0)
    {
        try
        {
            // MAV_CMD_DO_MOTOR_TEST (209)
            var command = CreateCommandLong(
                209,
                motorInstance,    // param1: Motor instance (1-based)
                throttleType,     // param2: Throttle type (0 = PWM value, 1 = % 0-100)
                throttle,         // param3: Throttle value (e.g. 1200 or 15)
                timeoutSeconds,   // param4: Timeout in seconds
                motorCount,       // param5: Motor count
                0, 0
            );

            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Motor test failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendPusherMotorTestAsync(float pwm = 1200, float timeoutSeconds = 5)
    {
        // Pusher motor is typically motor instance 5 or 9 depending on VTOL config. Send motor test on instance 5 with PWM throttle type
        return await SendMotorTestAsync(5, pwm, timeoutSeconds, 1, 0);
    }

    // Emergency Motor Cutoff / In-Air Force Disarm & Flight Termination
    public async Task<bool> SendEmergencyStopAsync()
    {
        try
        {
            // ArduPilot force disarm magic key = 21196
            var forceDisarmCmd = CreateCommandLong(
                (int)MavCmd.ComponentArmDisarm,
                0.0f,    // param1: 0 = disarm
                21196.0f, // param2: force disarm magic number
                0, 0, 0, 0, 0
            );
            SendMessage(forceDisarmCmd);

            // Send Flight Termination (MAV_CMD_DO_FLIGHTTERMINATION = 185)
            var termCmd = CreateCommandLong(185, 1.0f, 0, 0, 0, 0, 0, 0);
            SendMessage(termCmd);

            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Emergency stop failed: {ex.Message}");
            return false;
        }
    }

    // ArduPilot Compass Calibration Standard MAVLink Commands (42424, 42425, 42426)
    public async Task<bool> StartCompassCalibration42424Async(byte magMask = 0)
    {
        try
        {
            // MAV_CMD_DO_START_MAG_CAL (42424)
            var command = CreateCommandLong(
                42424,
                magMask, // param1: Mag mask (0 = all)
                1,       // param2: Retry on failure
                1,       // param3: Autosave
                0,       // param4: Delay
                0,       // param5: Autoreboot
                0, 0
            );

            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Start mag cal 42424 failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> AcceptCompassCalibration42425Async(byte magMask = 0)
    {
        try
        {
            // MAV_CMD_DO_ACCEPT_MAG_CAL (42425)
            var command = CreateCommandLong(42425, magMask, 0, 0, 0, 0, 0, 0);
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Accept mag cal 42425 failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> CancelCompassCalibration42426Async(byte magMask = 0)
    {
        try
        {
            // MAV_CMD_DO_CANCEL_MAG_CAL (42426)
            var command = CreateCommandLong(42426, magMask, 0, 0, 0, 0, 0, 0);
            SendMessage(command);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandSender] Cancel mag cal 42426 failed: {ex.Message}");
            return false;
        }
    }
    
    private UasCommandLong CreateCommandLong(int command, float param1, float param2,
                                              float param3, float param4, float param5,
                                              float param6, float param7)
    {
        return new UasCommandLong
        {
            Command = (MavCmd)command,
            TargetSystem = _service.GetTargetSystemId(),
            TargetComponent = _service.GetTargetComponentId(),
            Confirmation = 0,
            Param1 = param1,
            Param2 = param2,
            Param3 = param3,
            Param4 = param4,
            Param5 = param5,
            Param6 = param6,
            Param7 = param7
        };
    }
}
