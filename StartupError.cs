using System;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Security;

namespace erp_inteligente;

/// <summary>Turns startup failures into messages for the operator, without technical details or key material.</summary>
internal static class StartupError
{
    public static string Describe(Exception exception) => exception switch
    {
        MissingDatabaseKeyException =>
            "A chave deste banco de dados não está no cofre do sistema. Sem ela os dados não podem ser lidos, " +
            "e o Nexus não cria uma chave nova para um banco existente. Restaure o cofre ou um backup.",
        SecretStoreUnavailableException =>
            "O cofre de chaves do sistema não respondeu. Verifique se o serviço de chaves está ativo e desbloqueado e abra o Nexus de novo.",
        PlatformNotSupportedException => exception.Message,
        DatabaseUnavailableException =>
            "O banco de dados não pôde ser aberto: o arquivo está ilegível, danificado ou é de uma versão incompatível.",
        _ => "Falha inesperada ao iniciar o Nexus.",
    };
}
