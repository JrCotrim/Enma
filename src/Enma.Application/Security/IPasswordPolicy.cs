namespace Enma.Application.Security;

public interface IPasswordPolicy
{
    void Validate(string password);
}
