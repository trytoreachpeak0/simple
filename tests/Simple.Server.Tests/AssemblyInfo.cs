using System.Runtime.Versioning;

// The test terminals' keys live in the Windows key store (CNG), as real
// terminals' and the server's do; SIMPLE's machines are all Windows.
[assembly: SupportedOSPlatform("windows")]
