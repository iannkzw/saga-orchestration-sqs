namespace IntegrationTests.Infrastructure;

[CollectionDefinition("Integration")]
public sealed class IntegrationTestCollection : ICollectionFixture<DockerComposeFixture>
{
    // Classe vazia — apenas declara que os testes com [Collection("Integration")]
    // compartilham uma única instância de DockerComposeFixture (compose sobe uma vez).
}
