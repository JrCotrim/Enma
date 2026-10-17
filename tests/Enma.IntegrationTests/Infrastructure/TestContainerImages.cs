namespace Enma.IntegrationTests.Infrastructure;

// Container images for the integration tests, pinned by tag and digest to the
// images the deploy stacks run: PostgreSQL as in deploy/compose.yaml, Mailpit
// as in deploy/compose.pilot.yaml and MinIO as in both. The E2E stack
// (tests/Enma.E2ETests/Infrastructure/E2EStack.cs) pins the same references.
internal static class TestContainerImages
{
    public const string PostgreSql = "postgres:18.6-alpine3.24@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873";
    public const string Mailpit = "axllent/mailpit:v1.30.7@sha256:d5ecbb067db3705fa953d79e1b7f81ef84038df67aba6c52825d8c02a1ea748a";
    public const string Minio = "docker.io/pgsty/silo:RELEASE.2026-09-16T00-00-00Z@sha256:635197cb9f36d01bee221d34d1c7d7960f6a95c48b0b6c01d99cd13bdae51a46";
}
