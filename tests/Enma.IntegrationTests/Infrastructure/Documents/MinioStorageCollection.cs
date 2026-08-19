namespace Enma.IntegrationTests.Infrastructure.Documents;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MinioStorageCollection
{
    public const string Name = "MinIO object storage";
}
