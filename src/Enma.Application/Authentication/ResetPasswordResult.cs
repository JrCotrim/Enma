namespace Enma.Application.Authentication;

public enum ResetPasswordResult
{
    Invalid = 0,
    Succeeded = 1,
    CurrentPasswordReuse = 2
}
