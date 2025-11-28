using System;

namespace QuantPairs.Core.Errors;

public sealed class DataFormatException : Exception
{
    public DataFormatException(string message) : base(message) { }
    public DataFormatException(string message, Exception inner) : base(message, inner) { }
}
