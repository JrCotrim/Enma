namespace Enma.Application.Users;

public sealed class UserEmailAlreadyExistsException : InvalidOperationException
{
    public UserEmailAlreadyExistsException(string email)
        : this(email, null)
    {
    }

    public UserEmailAlreadyExistsException(
        string email,
        Exception? innerException)
        : base("A user with the provided email already exists.", innerException)
    {
        ArgumentNullException.ThrowIfNull(email);

        Email = email;
    }

    public string Email { get; }
}
