using System.Runtime.Versioning;

// The fake server's certificate key lives in the Windows key store (CNG), as
// the real server's does; SIMPLE's machines are all Windows.
[assembly: SupportedOSPlatform("windows")]
