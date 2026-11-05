namespace TransportAnalyzer.Api.Query;

/// <summary>Either a value or an error; used for request validation without exceptions.</summary>
public readonly record struct Result<T, TError>
{
    private readonly T? _value;
    private readonly TError? _error;

    private Result(bool isSuccess, T? value, TError? error) => (IsSuccess, _value, _error) = (isSuccess, value, error);

    public bool IsSuccess { get; }
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result is an error.");
    public TError Error => !IsSuccess ? _error! : throw new InvalidOperationException("Result is a success.");

    public static Result<T, TError> Ok(T value) => new(true, value, default);
    public static Result<T, TError> Fail(TError error) => new(false, default, error);

    public static implicit operator Result<T, TError>(T value) => Ok(value);
}
