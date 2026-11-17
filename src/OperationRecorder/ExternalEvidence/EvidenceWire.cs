using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.ExternalEvidence;

public static class EvidenceWire
{
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var length = new byte[4]; await stream.ReadExactlyAsync(length, token);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count < 2 || count > EvidenceProtocol.MaxMessageBytes) throw new EvidenceUnavailableException("invalid-request");
        var payload = new byte[count]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload, EvidenceProtocol.Json) ?? throw new EvidenceUnavailableException("invalid-request");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, EvidenceProtocol.Json);
        if (payload.Length > EvidenceProtocol.MaxMessageBytes) throw new EvidenceUnavailableException("message-limit");
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
    public static async Task<byte[]> ReadBoundedToEndAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream(); byte[] block = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(block, token)) > 0)
        { if (output.Length + count > maximum) throw new EvidenceUnavailableException("message-limit"); await output.WriteAsync(block.AsMemory(0,count), token); }
        return output.ToArray();
    }
}
