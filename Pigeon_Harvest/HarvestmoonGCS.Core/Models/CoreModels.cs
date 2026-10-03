using System.ComponentModel;
using System.Runtime.CompilerServices;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Models;

/// <summary>
/// Base class untuk tipe device
/// </summary>
public class TipeWahana
{
    public TipeDevice Tipe { get; set; }
}

/// <summary>
/// Data sensor IMU (Inertial Measurement Unit)
/// Mendeteksi kemiringan dan orientasi objek
/// </summary>
public class Inertial : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private float _yaw;
    private float _pitch;
    private float _roll;

    /// <summary>
    /// Arah horizontal wahana (Heading).
    /// Nilai dalam satuan Derajat (°)
    /// </summary>
    public float Yaw
    {
        get => _yaw;
        set => SetProperty(ref _yaw, value);
    }

    /// <summary>
    /// Sudut kecuraman wahana (Nose/Tail).
    /// Nilai dalam satuan Derajat (°)
    /// </summary>
    public float Pitch
    {
        get => _pitch;
        set => SetProperty(ref _pitch, value);
    }

    /// <summary>
    /// Sudut kemiringan sayap wahana.
    /// Nilai dalam satuan Derajat (°)
    /// </summary>
    public float Roll
    {
        get => _roll;
        set => SetProperty(ref _roll, value);
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Data GPS wahana
/// </summary>
public class GPSData : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private int _latitude;
    private int _longitude;
    private float _hdop;
    private int _sats;

    /// <summary>
    /// Apakah data GPS valid (koordinat tidak nol)
    /// </summary>
    public bool IsValid => Latitude != 0 && Longitude != 0;

    /// <summary>
    /// Koordinat Latitude dalam format Decimal Degrees
    /// Latitude = lintang
    /// </summary>
    public int Latitude
    {
        get => _latitude;
        set => SetProperty(ref _latitude, value);
    }

    /// <summary>
    /// Koordinat Longitude dalam format Decimal Degrees
    /// Longitude = Bujur
    /// </summary>
    public int Longitude
    {
        get => _longitude;
        set => SetProperty(ref _longitude, value);
    }

    /// <summary>
    /// Horizontal Dilution of Precision (semakin kecil semakin baik)
    /// hdop = Horizontal Dilution of Precision
    /// </summary>
    public float Hdop
    {
        get => _hdop;
        set => SetProperty(ref _hdop, value);
    }

    /// <summary>
    /// Jumlah satelit yang terhubung (semakin banyak semakin baik)
    /// sats = jumlah satelit yang terhubung
    /// </summary>
    public int Sats
    {
        get => _sats;
        set => SetProperty(ref _sats, value);
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Data GPS untuk Tracker dengan koordinat double precision
/// </summary>
public class GPSDataTracker : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private double _latitude;
    private double _longitude;

    /// <summary>
    /// Apakah data GPS valid (koordinat tidak nol)
    /// </summary>
    public bool IsValid => Latitude != 0 && Longitude != 0;

    /// <summary>
    /// Koordinat Latitude dalam format Decimal Degrees
    /// Latitude = lintang
    /// </summary>
    public double Latitude
    {
        get => _latitude;
        set => SetProperty(ref _latitude, value);
    }

    /// <summary>
    /// Koordinat Longitude dalam format Decimal Degrees
    /// Longitude = Bujur
    /// </summary>
    public double Longitude
    {
        get => _longitude;
        set => SetProperty(ref _longitude, value);
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Parameter tuning PID untuk kontrol wahana
/// </summary>
public class TuningParameters : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private float _roll_P;
    private float _roll_I;
    private float _roll_D;
    private float _roll_IMAX;
    private float _roll_FILT;
    private float _roll_FLTD;
    private float _roll_FLTT;

    private float _pitch_P;
    private float _pitch_I;
    private float _pitch_D;
    private float _pitch_IMAX;
    private float _pitch_FILT;
    private float _pitch_FLTD;
    private float _pitch_FLTT;

    private float _yaw_P;
    private float _yaw_I;
    private float _yaw_D;
    private float _yaw_IMAX;
    private float _yaw_FILT;
    private float _yaw_FLTD;
    private float _yaw_FLTT;

    private float _vel_P;
    private float _vel_I;
    private float _vel_D;
    private int _vel_IMAX;

    private int _pitchMin;
    private int _pitchMax;
    private int _rollLim;

    // Roll PID parameters
    public float Roll_P { get => _roll_P; set => SetProperty(ref _roll_P, value); }
    public float Roll_I { get => _roll_I; set => SetProperty(ref _roll_I, value); }
    public float Roll_D { get => _roll_D; set => SetProperty(ref _roll_D, value); }
    public float Roll_IMAX { get => _roll_IMAX; set => SetProperty(ref _roll_IMAX, value); }
    public float Roll_FILT { get => _roll_FILT; set => SetProperty(ref _roll_FILT, value); }
    public float Roll_FLTD { get => _roll_FLTD; set => SetProperty(ref _roll_FLTD, value); }
    public float Roll_FLTT { get => _roll_FLTT; set => SetProperty(ref _roll_FLTT, value); }

    // Pitch PID parameters
    public float Pitch_P { get => _pitch_P; set => SetProperty(ref _pitch_P, value); }
    public float Pitch_I { get => _pitch_I; set => SetProperty(ref _pitch_I, value); }
    public float Pitch_D { get => _pitch_D; set => SetProperty(ref _pitch_D, value); }
    public float Pitch_IMAX { get => _pitch_IMAX; set => SetProperty(ref _pitch_IMAX, value); }
    public float Pitch_FILT { get => _pitch_FILT; set => SetProperty(ref _pitch_FILT, value); }
    public float Pitch_FLTD { get => _pitch_FLTD; set => SetProperty(ref _pitch_FLTD, value); }
    public float Pitch_FLTT { get => _pitch_FLTT; set => SetProperty(ref _pitch_FLTT, value); }

    // Yaw PID parameters
    public float Yaw_P { get => _yaw_P; set => SetProperty(ref _yaw_P, value); }
    public float Yaw_I { get => _yaw_I; set => SetProperty(ref _yaw_I, value); }
    public float Yaw_D { get => _yaw_D; set => SetProperty(ref _yaw_D, value); }
    public float Yaw_IMAX { get => _yaw_IMAX; set => SetProperty(ref _yaw_IMAX, value); }
    public float Yaw_FILT { get => _yaw_FILT; set => SetProperty(ref _yaw_FILT, value); }
    public float Yaw_FLTD { get => _yaw_FLTD; set => SetProperty(ref _yaw_FLTD, value); }
    public float Yaw_FLTT { get => _yaw_FLTT; set => SetProperty(ref _yaw_FLTT, value); }

    // Velocity PID parameters
    public float Vel_P { get => _vel_P; set => SetProperty(ref _vel_P, value); }
    public float Vel_I { get => _vel_I; set => SetProperty(ref _vel_I, value); }
    public float Vel_D { get => _vel_D; set => SetProperty(ref _vel_D, value); }
    public int Vel_IMAX { get => _vel_IMAX; set => SetProperty(ref _vel_IMAX, value); }

    // Pitch and Roll limits
    public int PitchMin { get => _pitchMin; set => SetProperty(ref _pitchMin, value); }
    public int PitchMax { get => _pitchMax; set => SetProperty(ref _pitchMax, value); }
    public int RollLim { get => _rollLim; set => SetProperty(ref _rollLim, value); }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Data waypoint untuk mission planning
/// </summary>
public class Waypoint : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private float _speed;
    private float _loiterSpeed;
    private float _radius;
    private float _targetLongt2;
    private float _speedDn;
    private float _speedUp;

    /// <summary>
    /// Kecepatan waypoint
    /// </summary>
    public float Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    /// <summary>
    /// Kecepatan loiter
    /// </summary>
    public float LoiterSpeed
    {
        get => _loiterSpeed;
        set => SetProperty(ref _loiterSpeed, value);
    }

    /// <summary>
    /// Radius waypoint
    /// </summary>
    public float Radius
    {
        get => _radius;
        set => SetProperty(ref _radius, value);
    }

    /// <summary>
    /// Target longitude 2
    /// </summary>
    public float TargetLongt2
    {
        get => _targetLongt2;
        set => SetProperty(ref _targetLongt2, value);
    }

    /// <summary>
    /// Kecepatan turun
    /// </summary>
    public float SpeedDn
    {
        get => _speedDn;
        set => SetProperty(ref _speedDn, value);
    }

    /// <summary>
    /// Kecepatan naik
    /// </summary>
    public float SpeedUp
    {
        get => _speedUp;
        set => SetProperty(ref _speedUp, value);
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


/// <summary>
/// Data telemetri lengkap dari wahana UAV
/// </summary>
public class FlightData : TipeWahana, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private FlightMode _flightMode = FlightMode.MANUAL;
    private TuningParameters _tuning = new TuningParameters();
    private ushort _batteryVolt;
    private ushort _batteryCurr;
    private ushort _mavlinkMiliVolt;
    private short _mavlinkCentiAmp;
    private float _barometers;
    private int _type;
    private int _compassProgress1;
    private int _compassProgress2;
    private int _modeChannel;
    private int _modeCh1;
    private int _modeCh2;
    private int _modeCh3;
    private int _modeCh4;
    private int _modeCh5;
    private int _modeCh6;
    private int _modeCh5PWM;
    private int _modeCh6PWM;
    private int _modeCh7PWM;
    private int _modeCh8PWM;
    private int _modeCh9PWM;
    private int _modeCh10PWM;
    private int _modeCh11PWM;
    private int _modeCh12PWM;
    private int _modeCh13PWM;
    private int _modeCh14PWM;
    private int _modeCh15PWM;
    private int _modeCh16PWM;
    private int _servoCh1;
    private int _servoCh2;
    private int _servoCh3;
    private int _servoCh4;
    private int _servoCh5;
    private int _servoCh6;
    private int _servoCh7;
    private int _servoCh8;
    private int _servoCh9;
    private int _servoCh10;
    private int _servoCh11;
    private int _servoCh12;
    private int _servoCh13;
    private int _servoCh14;
    private int _servoCh15;
    private int _servoCh16;
    private byte _signal;
    private Inertial _imu = new Inertial();
    private int _altitude;
    private float _altitudeFloat;
    private float _speed;
    private float _groundSpeed;
    private float _verticalSpeed;
    private bool _isArmed;
    private int _batteryRemaining = -1;
    private GPSData _gps = new GPSData();
    private Waypoint _wpoint = new Waypoint();
    private byte _sats;
    private ushort _hdop;
    private int _throttlePercent;

    public FlightData()
    {
        // Set default vehicle type to FixedWing (plane)
        _type = 1; // MavType.FixedWing
        
        // Subscribe to nested object property changes to propagate to parent
        _imu.PropertyChanged += (s, e) => OnPropertyChanged(nameof(IMU));
        _gps.PropertyChanged += (s, e) => OnPropertyChanged(nameof(GPS));
        _wpoint.PropertyChanged += (s, e) => OnPropertyChanged(nameof(Wpoint));
        _tuning.PropertyChanged += (s, e) => OnPropertyChanged(nameof(Tuning));
    }

    public int ThrottlePercent
    {
        get => _throttlePercent;
        set => SetProperty(ref _throttlePercent, value);
    }

    /// <summary>
    /// Ground speed dari VFR_HUD.groundspeed (m/s)
    /// </summary>
    public float GroundSpeed
    {
        get => _groundSpeed;
        set => SetProperty(ref _groundSpeed, value);
    }

    /// <summary>
    /// Vertical speed (climb rate) dari VFR_HUD.climb (m/s, positif = naik)
    /// </summary>
    public float VerticalSpeed
    {
        get => _verticalSpeed;
        set => SetProperty(ref _verticalSpeed, value);
    }

    /// <summary>
    /// Armed state dari HEARTBEAT.base_mode MAV_MODE_FLAG_SAFETY_ARMED bit
    /// </summary>
    public bool IsArmed
    {
        get => _isArmed;
        set => SetProperty(ref _isArmed, value);
    }

    /// <summary>
    /// Battery remaining percentage dari SYS_STATUS.battery_remaining (-1 = unknown)
    /// </summary>
    public int BatteryRemaining
    {
        get => _batteryRemaining;
        set => SetProperty(ref _batteryRemaining, value);
    }

    public FlightMode FlightMode
    {
        get => _flightMode;
        set => SetProperty(ref _flightMode, value);
    }

    public TuningParameters Tuning
    {
        get => _tuning;
        set => SetProperty(ref _tuning, value);
    }

    public ushort BatteryVolt
    {
        get => _batteryVolt;
        set => SetProperty(ref _batteryVolt, value);
    }

    public ushort BatteryCurr
    {
        get => _batteryCurr;
        set => SetProperty(ref _batteryCurr, value);
    }

    public ushort MavlinkMiliVolt
    {
        get => _mavlinkMiliVolt;
        set => SetProperty(ref _mavlinkMiliVolt, value);
    }

    public short MavlinkCentiAmp
    {
        get => _mavlinkCentiAmp;
        set => SetProperty(ref _mavlinkCentiAmp, value);
    }

    public float Barometers
    {
        get => _barometers;
        set => SetProperty(ref _barometers, value);
    }

    /// <summary>
    /// MAV_TYPE from HEARTBEAT message - identifies vehicle type (FixedWing=1, Quadrotor=2, etc.)
    /// </summary>
    public int Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    /// <summary>
    /// Alias for Type property - MAV_TYPE from HEARTBEAT (FixedWing=1, Quadrotor=2, etc.)
    /// </summary>
    public int VehicleType
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    public int Compass_Progress1
    {
        get => _compassProgress1;
        set => SetProperty(ref _compassProgress1, value);
    }

    public int Compass_Progress2
    {
        get => _compassProgress2;
        set => SetProperty(ref _compassProgress2, value);
    }

    public int ModeChannel
    {
        get => _modeChannel;
        set => SetProperty(ref _modeChannel, value);
    }

    public int ModeCh1 { get => _modeCh1; set => SetProperty(ref _modeCh1, value); }
    public int ModeCh2 { get => _modeCh2; set => SetProperty(ref _modeCh2, value); }
    public int ModeCh3 { get => _modeCh3; set => SetProperty(ref _modeCh3, value); }
    public int ModeCh4 { get => _modeCh4; set => SetProperty(ref _modeCh4, value); }
    public int ModeCh5 { get => _modeCh5; set => SetProperty(ref _modeCh5, value); }
    public int ModeCh6 { get => _modeCh6; set => SetProperty(ref _modeCh6, value); }

    public int ModeCh5PWM { get => _modeCh5PWM; set => SetProperty(ref _modeCh5PWM, value); }
    public int ModeCh6PWM { get => _modeCh6PWM; set => SetProperty(ref _modeCh6PWM, value); }
    public int ModeCh7PWM { get => _modeCh7PWM; set => SetProperty(ref _modeCh7PWM, value); }
    public int ModeCh8PWM { get => _modeCh8PWM; set => SetProperty(ref _modeCh8PWM, value); }
    public int ModeCh9PWM { get => _modeCh9PWM; set => SetProperty(ref _modeCh9PWM, value); }
    public int ModeCh10PWM { get => _modeCh10PWM; set => SetProperty(ref _modeCh10PWM, value); }
    public int ModeCh11PWM { get => _modeCh11PWM; set => SetProperty(ref _modeCh11PWM, value); }
    public int ModeCh12PWM { get => _modeCh12PWM; set => SetProperty(ref _modeCh12PWM, value); }
    public int ModeCh13PWM { get => _modeCh13PWM; set => SetProperty(ref _modeCh13PWM, value); }
    public int ModeCh14PWM { get => _modeCh14PWM; set => SetProperty(ref _modeCh14PWM, value); }
    public int ModeCh15PWM { get => _modeCh15PWM; set => SetProperty(ref _modeCh15PWM, value); }
    public int ModeCh16PWM { get => _modeCh16PWM; set => SetProperty(ref _modeCh16PWM, value); }

    public int ServoCh1 { get => _servoCh1; set => SetProperty(ref _servoCh1, value); }
    public int ServoCh2 { get => _servoCh2; set => SetProperty(ref _servoCh2, value); }
    public int ServoCh3 { get => _servoCh3; set => SetProperty(ref _servoCh3, value); }
    public int ServoCh4 { get => _servoCh4; set => SetProperty(ref _servoCh4, value); }
    public int ServoCh5 { get => _servoCh5; set => SetProperty(ref _servoCh5, value); }
    public int ServoCh6 { get => _servoCh6; set => SetProperty(ref _servoCh6, value); }
    public int ServoCh7 { get => _servoCh7; set => SetProperty(ref _servoCh7, value); }
    public int ServoCh8 { get => _servoCh8; set => SetProperty(ref _servoCh8, value); }
    public int ServoCh9 { get => _servoCh9; set => SetProperty(ref _servoCh9, value); }
    public int ServoCh10 { get => _servoCh10; set => SetProperty(ref _servoCh10, value); }
    public int ServoCh11 { get => _servoCh11; set => SetProperty(ref _servoCh11, value); }
    public int ServoCh12 { get => _servoCh12; set => SetProperty(ref _servoCh12, value); }
    public int ServoCh13 { get => _servoCh13; set => SetProperty(ref _servoCh13, value); }
    public int ServoCh14 { get => _servoCh14; set => SetProperty(ref _servoCh14, value); }
    public int ServoCh15 { get => _servoCh15; set => SetProperty(ref _servoCh15, value); }
    public int ServoCh16 { get => _servoCh16; set => SetProperty(ref _servoCh16, value); }

    /// <summary>
    /// Kualitas sinyal dari perhitungan paket data yang dibuang.
    /// Nilai dalam satuan Persen (%) dengan rentang 0-255.
    /// </summary>
    public byte Signal
    {
        get => _signal;
        set => SetProperty(ref _signal, value);
    }

    /// <summary>
    /// Data sensor IMU
    /// IMU = Mendeteksi kemiringan objek
    /// </summary>
    public Inertial IMU
    {
        get => _imu;
        set => SetProperty(ref _imu, value);
    }

    /// <summary>
    /// Ketinggian wahana dari permukaan laut (dpl)/(MSL).
    /// Nilai dalam satuan Milimeter (mm).
    /// </summary>
    public int Altitude
    {
        get => _altitude;
        set => SetProperty(ref _altitude, value);
    }

    public float AltitudeFloat
    {
        get => _altitudeFloat;
        set => SetProperty(ref _altitudeFloat, value);
    }

    /// <summary>
    /// Kecepatan wahana terhadap tanah.
    /// Nilai dalam satuan Meter per Sec (m/s)
    /// </summary>
    public float Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    /// <summary>
    /// Data sensor GPS
    /// </summary>
    public GPSData GPS
    {
        get => _gps;
        set => SetProperty(ref _gps, value);
    }

    /// <summary>
    /// Target dari waypoint
    /// </summary>
    public Waypoint Wpoint
    {
        get => _wpoint;
        set => SetProperty(ref _wpoint, value);
    }

    public byte Sats
    {
        get => _sats;
        set => SetProperty(ref _sats, value);
    }

    public ushort Hdop
    {
        get => _hdop;
        set => SetProperty(ref _hdop, value);
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Data telemetri dari Antenna Tracker
/// </summary>
public class TrackerData : TipeWahana, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private Inertial _imu = new Inertial();
    private float _battery;
    private GPSData _gps = new GPSData();
    private GPSDataTracker _gpsTrack = new GPSDataTracker();
    private double _yaw;
    private double _tes2;
    private float _altitude;

    public TrackerData()
    {
        // Subscribe to nested object property changes to propagate to parent
        _imu.PropertyChanged += (s, e) => OnPropertyChanged(nameof(IMU));
        _gps.PropertyChanged += (s, e) => OnPropertyChanged(nameof(GPS));
        _gpsTrack.PropertyChanged += (s, e) => OnPropertyChanged(nameof(GPSTrack));
    }

    /// <summary>
    /// Data sensor IMU tracker
    /// </summary>
    public Inertial IMU
    {
        get => _imu;
        set => SetProperty(ref _imu, value);
    }

    /// <summary>
    /// Level baterai tracker
    /// </summary>
    public float Battery
    {
        get => _battery;
        set => SetProperty(ref _battery, value);
    }

    /// <summary>
    /// Data GPS tracker (format integer)
    /// </summary>
    public GPSData GPS
    {
        get => _gps;
        set => SetProperty(ref _gps, value);
    }

    /// <summary>
    /// Data GPS tracker (format double precision)
    /// </summary>
    public GPSDataTracker GPSTrack
    {
        get => _gpsTrack;
        set => SetProperty(ref _gpsTrack, value);
    }

    /// <summary>
    /// Yaw tracker
    /// </summary>
    public double Yaw
    {
        get => _yaw;
        set => SetProperty(ref _yaw, value);
    }

    public double Tes2
    {
        get => _tes2;
        set => SetProperty(ref _tes2, value);
    }

    /// <summary>
    /// Ketinggian tracker (Meter)
    /// </summary>
    public float Altitude
    {
        get => _altitude;
        set => SetProperty(ref _altitude, value);
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
