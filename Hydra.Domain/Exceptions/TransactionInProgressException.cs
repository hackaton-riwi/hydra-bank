namespace Hydra.Domain.Exceptions;

public class TransactionInProgressException : InvalidOperationException
{
    public TransactionInProgressException(string idempotencyKey)
        : base($"Ya hay una transaccion en proceso para la key {idempotencyKey}")
    {
    }
}
