namespace IPABridge.Models;

public sealed record IpatoolLoginResult(bool Success, bool RequiresTwoFactor, string Message);
