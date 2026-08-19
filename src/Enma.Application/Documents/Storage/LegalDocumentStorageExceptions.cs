namespace Enma.Application.Documents.Storage;

public abstract class LegalDocumentStorageException : Exception
{
    protected LegalDocumentStorageException(string message)
        : base(message)
    {
    }
}

public sealed class LegalDocumentStorageObjectNotFoundException
    : LegalDocumentStorageException
{
    public LegalDocumentStorageObjectNotFoundException()
        : base("The requested document storage object was not found.")
    {
    }
}

public sealed class LegalDocumentStorageUnavailableException
    : LegalDocumentStorageException
{
    public LegalDocumentStorageUnavailableException()
        : base("Document storage is temporarily unavailable.")
    {
    }
}
