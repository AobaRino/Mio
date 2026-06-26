using System;

namespace Mio.Player;

public sealed class MpvException : Exception
{
    public MpvException(string message)
        : base(message)
    {
    }

    public MpvException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
