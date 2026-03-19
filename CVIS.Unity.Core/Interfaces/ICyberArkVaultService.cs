using System;
using System.Linq;

namespace CVIS.Unity.Core.Interfaces
{
    public interface ICyberArkVaultService
    {
        /// <summary>
        /// Retrieves the Platform ZIP package from the Vault.
        /// </summary>
        Task<Stream> GetPlatformPackageAsync(string platformId);
    }
}
