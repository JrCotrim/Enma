namespace Enma.Application.Users;

public sealed class UserEmailAlreadyExistsException : InvalidOperationException
{
    public UserEmailAlreadyExistsException(string email)
        : base("A user with the provided email already exists.")
    {
        ArgumentNullException.ThrowIfNull(email);

        Email = email;
    }

    public string Email { get; }
}
