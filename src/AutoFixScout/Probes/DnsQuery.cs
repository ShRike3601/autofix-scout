using System.Buffers.Binary;
using System.Text;

namespace AutoFixScout.Probes;

/// <summary>How a DNS reply was judged. Only <see cref="Ok"/> and <see cref="Truncated"/> count as answered.</summary>
public enum DnsReplyStatus
{
    /// <summary>Right transaction id, response flag set, RCODE 0.</summary>
    Ok,
    /// <summary>As <see cref="Ok"/>, but the server set the truncation flag (the full answer would not fit in one UDP packet). The server did answer.</summary>
    Truncated,
    /// <summary>RCODE 3: the name does not exist. For the fixed list of common names this means a broken or tampered server, so it is not an answer.</summary>
    NameError,
    /// <summary>RCODE 2 (server failure).</summary>
    ServerFailure,
    /// <summary>RCODE 5 (refused).</summary>
    Refused,
    /// <summary>Any other non-zero RCODE.</summary>
    OtherError,
    /// <summary>The transaction id differs from the query: not the reply to this query.</summary>
    WrongId,
    /// <summary>The packet is a query, not a response.</summary>
    NotAResponse,
    /// <summary>Shorter than a DNS header, or null.</summary>
    Malformed,
}

public readonly record struct DnsReply(DnsReplyStatus Status, int RCode, int AnswerCount)
{
    public bool Answered => Status is DnsReplyStatus.Ok or DnsReplyStatus.Truncated;
}

/// <summary>
/// A tiny DNS query builder and reply parser (RFC 1035): one question, type A, class IN, recursion desired.
/// Pure and unit tested. Nothing here touches the network.
/// </summary>
public static class DnsQuery
{
    public const int HeaderLength = 12;

    /// <summary>The query packet for an A record of <paramref name="name"/>. Throws ArgumentException for a name that is not valid ASCII host name syntax.</summary>
    public static byte[] Build(ushort id, string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 253) throw new ArgumentException("Invalid DNS name.", nameof(name));
        var labels = name.TrimEnd('.').Split('.');
        var ms = new MemoryStream();
        Span<byte> head = stackalloc byte[HeaderLength];
        head.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(head, id);
        BinaryPrimitives.WriteUInt16BigEndian(head[2..], 0x0100); // standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(head[4..], 1);      // one question
        ms.Write(head);
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label.Any(c => c > 127)) throw new ArgumentException("Invalid DNS name.", nameof(name));
            ms.WriteByte((byte)label.Length);
            ms.Write(Encoding.ASCII.GetBytes(label));
        }
        ms.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, 1);      // type A
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1); // class IN
        ms.Write(tail);
        return ms.ToArray();
    }

    /// <summary>Judges a received packet against the id that was sent.</summary>
    public static DnsReply Parse(byte[]? packet, ushort expectedId)
    {
        if (packet == null || packet.Length < HeaderLength) return new(DnsReplyStatus.Malformed, -1, 0);
        var id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6));
        var rcode = flags & 0xF;
        if (id != expectedId) return new(DnsReplyStatus.WrongId, rcode, answers);
        if ((flags & 0x8000) == 0) return new(DnsReplyStatus.NotAResponse, rcode, answers);
        var status = rcode switch
        {
            0 => (flags & 0x0200) != 0 ? DnsReplyStatus.Truncated : DnsReplyStatus.Ok,
            2 => DnsReplyStatus.ServerFailure,
            3 => DnsReplyStatus.NameError,
            5 => DnsReplyStatus.Refused,
            _ => DnsReplyStatus.OtherError,
        };
        return new(status, rcode, answers);
    }
}
