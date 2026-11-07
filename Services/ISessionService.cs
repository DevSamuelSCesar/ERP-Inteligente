using erp_inteligente.Models;

namespace erp_inteligente.Services;

public interface ISessionService
{
    UserSession Current { get; }
}
