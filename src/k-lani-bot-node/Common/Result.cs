namespace KLaniBot.Common;

// Use case: reading something from outside the node (a file an operator edited, a value on the wire) fails in ordinary
// ways. The failure is part of the return type, so a caller sees every way it can go wrong without reading the code
// behind it, and has to decide what each one means before it gets the value.
public sealed class Result<TValue, TError>
    where TValue : notnull
    where TError : notnull
{
    private readonly TValue? _value;
    private readonly TError? _error;

    private Result(TValue? value, TError? error, bool isSuccess)
    {
        _value = value;
        _error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess
    {
        get;
    }

    public static Result<TValue, TError> Success(TValue value)
    {
        return new Result<TValue, TError>(value, default, true);
    }

    public static Result<TValue, TError> Failure(TError error)
    {
        return new Result<TValue, TError>(default, error, false);
    }

    public static implicit operator Result<TValue, TError>(TValue value)
    {
        return Success(value);
    }

    public static implicit operator Result<TValue, TError>(TError error)
    {
        return Failure(error);
    }

    // The only way to the value: both outcomes are named at the call.
    public TResult Match<TResult>(Func<TValue, TResult> onSuccess, Func<TError, TResult> onFailure)
    {
        return IsSuccess ? onSuccess(_value!) : onFailure(_error!);
    }
}
