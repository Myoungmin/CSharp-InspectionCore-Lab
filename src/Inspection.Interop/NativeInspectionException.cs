namespace Inspection.Interop;

public enum NativeStatus
{
    Ok = 0,
    InvalidArgument = 1,
    OutOfMemory = 2,
    InternalError = 3
}

public enum NativeFaultMode
{
    None = 0,
    ThrowOnCreate = 1,
    ThrowOnInspect = 2
}

public sealed class NativeInspectionException : Exception
{
    public NativeInspectionException(string operation, NativeStatus status)
        : base($"Native {operation} failed: {status} ({(int)status}).")
    {
        Operation = operation;
        Status = status;
    }

    public string Operation { get; }
    public NativeStatus Status { get; }
}
