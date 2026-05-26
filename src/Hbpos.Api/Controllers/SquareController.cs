using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Square;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/square")]
[Authorize]
public sealed class SquareController(ISquareTokenService squareTokenService) : ControllerBase
{
    private const string TokenReadFailedCode = "SQUARE_TOKEN_READ_FAILED";
    private const string TokenReadFailedMessage = "Failed to load Square token configuration.";

    [HttpGet("token")]
    public async Task<ActionResult<ApiResult<SquareTokenResponse>>> GetToken(
        [FromQuery] string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = SquareTokenService.NormalizeEnvironment(environment);
        if (normalizedEnvironment is null)
        {
            return BadRequest(ApiResult<SquareTokenResponse>.Fail(
                "SQUARE_ENVIRONMENT_INVALID",
                "environment must be Production or Sandbox"));
        }

        try
        {
            var token = await squareTokenService.GetActiveTokenAsync(normalizedEnvironment, cancellationToken);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return NotFound(ApiResult<SquareTokenResponse>.Fail(
                    "SQUARE_TOKEN_NOT_CONFIGURED",
                    "Square token is not configured for this environment."));
            }

            return Ok(ApiResult<SquareTokenResponse>.Ok(token));
        }
        catch
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResult<SquareTokenResponse>.Fail(
                    TokenReadFailedCode,
                    TokenReadFailedMessage));
        }
    }
}
