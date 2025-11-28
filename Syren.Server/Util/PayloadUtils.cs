using System.Buffers;
using System.Text;

namespace Syren.Server.Utils;

static class PayloadUtils
{
    public static string GetPayloadAsString(ReadOnlySequence<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return string.Empty;
        }

        if (payload.IsSingleSegment)
        {
            return Encoding.UTF8.GetString(payload.FirstSpan);
        }

        return Encoding.UTF8.GetString(payload.ToArray());
    }
}
