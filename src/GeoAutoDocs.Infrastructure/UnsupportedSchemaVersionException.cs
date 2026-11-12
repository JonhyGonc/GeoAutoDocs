namespace GeoAutoDocs.Infrastructure;

/// <summary>
/// O banco local foi criado ou atualizado por uma versão mais nova do GeoAutoDocs.
/// Abrir esse banco com esta build poderia gravar dados incompletos, então a abertura é recusada.
/// </summary>
public sealed class UnsupportedSchemaVersionException(int databaseVersion, int supportedVersion)
    : InvalidOperationException(
        $"O banco de dados usa o esquema {databaseVersion}, mas esta versão do GeoAutoDocs suporta até o esquema " +
        $"{supportedVersion}. Instale a versão mais recente do GeoAutoDocs.")
{
    public int DatabaseVersion { get; } = databaseVersion;
    public int SupportedVersion { get; } = supportedVersion;
}
