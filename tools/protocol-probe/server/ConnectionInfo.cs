using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Simple.ProtocolProbe.Server;

/// <summary>What one gRPC call negotiated: HTTP and TLS versions, cipher suite,
/// ALPN, and the hex SHA-256 of the client certificate's public key.</summary>
internal sealed record ConnectionInfo(
    string ConnectionId,
    string Remote,
    string HttpProtocol,
    string TlsProtocol,
    string CipherSuite,
    string Alpn,
    string? Key,
    string Major)
{
    public static ConnectionInfo From(ServerCallContext context)
    {
        var http = context.GetHttpContext();
        var tls = http.Features.Get<ITlsHandshakeFeature>();
        var alpn = http.Features.Get<ITlsApplicationProtocolFeature>()?.ApplicationProtocol;
        var certificate = http.Connection.ClientCertificate;
        return new ConnectionInfo(
            http.Connection.Id,
            $"{http.Connection.RemoteIpAddress}:{http.Connection.RemotePort}",
            http.Request.Protocol,
            tls?.Protocol.ToString() ?? "none",
            tls?.NegotiatedCipherSuite?.ToString() ?? "none",
            alpn is { Length: > 0 } a ? Encoding.ASCII.GetString(a.Span) : "none",
            certificate is null ? null : Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())),
            context.RequestHeaders.GetValue(ProtocolVersionInterceptor.MajorHeader) ?? "-");
    }
}

/// <summary>Field numbers in a message that this side's schema does not know.</summary>
internal static class UnknownFields
{
    public static string Of(IMessage message)
    {
        var numbers = new List<int>();
        var input = new CodedInputStream(message.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var number = WireFormat.GetTagFieldNumber(tag);
            if (message.Descriptor.FindFieldByNumber(number) is null)
            {
                numbers.Add(number);
            }

            input.SkipLastField();
        }

        return string.Join(',', numbers);
    }
}
