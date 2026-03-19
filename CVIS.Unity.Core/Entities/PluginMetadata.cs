namespace CVIS.Unity.Core.Entities
{
    public class PluginMetadata
    {
        public string FileName { get; set; }
        public string FileHash { get; set; } // SHA-256 String
        public long FileSize { get; set; }   // Extra validation layer
    }
}
