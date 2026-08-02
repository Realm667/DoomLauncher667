namespace DoomLauncher
{
    public class LaunchArgs
    {
        public string LaunchFileName { get; set; }
        public int? LaunchGameFileID { get; set; }
        public int? LaunchGameProfileID { get; set; }
        public int? LaunchSourcePortID { get; set; }
        public int? LaunchIWadID { get; set; }
        public int? EditGameFileID { get; set; }
        public bool LaunchDefaultProfile { get; set; }
        public bool OpenSettings { get; set; }
        public bool AutoClose { get; set; }
    }
}
