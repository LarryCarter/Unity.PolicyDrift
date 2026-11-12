using System;
using System.IO;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{/// <summary>
 /// Pure filesystem abstraction — stateless, config-free, cross-platform.
 /// No domain knowledge. No path derivation. Just OS-level I/O.
 /// Injected into domain services (PolicyDriftPathProvider, orchestrators, processors)
 /// that own the path logic themselves.
 /// </summary>
    public interface IFileSystemService
    {
        // ── Directory Operations ──────────────────────────────────
        bool DirectoryExists(string path);
        void CreateDirectory(string path);
        string[] GetFilesInDirectory(string path, string searchPattern);

        // ── File Operations ───────────────────────────────────────
        bool FileExists(string path);
        void MoveFile(string source, string destination);
        void DeleteFile(string path);
        Stream OpenRead(string path);
        string ReadAllText(string path);

        // ── Cleanup ───────────────────────────────────────────────
        void DeleteDirectory(string path, bool recursive);
    }
}