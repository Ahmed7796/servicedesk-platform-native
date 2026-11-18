using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using ServiceDesk.Api.Auth;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Api.Controllers
{
    [Route("api/auth")]
    [ApiController]
    public sealed class AuthController : ControllerBase
    {
        private readonly IUserRepository _users;
        private readonly PasswordHasher<User> _hasher = new();
        private readonly JwtTokenService _tokens;
        private readonly ServiceClientOptions _serviceClient;
        public AuthController(IUserRepository users, JwtTokenService tokens, ServiceClientOptions serviceClient)
        {
            _users = users;
            _tokens = tokens;
            _serviceClient = serviceClient;
        }
        // Successful Login: Generate token, return 200-OK, Else, Return 401-UNAUTHORIZED
        [HttpPost("login")][AllowAnonymous]
        public async Task<ActionResult<TokenResult>> Login(LoginDto dto, CancellationToken ct)
        {
            var user = await _users.GetByEmailOrEmployeeNumberAsync(dto.Identifier, ct);

            if (user is null || _hasher.VerifyHashedPassword(user, user.PasswordHash, dto.Password) == PasswordVerificationResult.Failed)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials.");

            return Ok(_tokens.CreateToken(user));   
        }

        // Client-credentials exchange for the triage copilot. An unknown id and a wrong secret look identical.
        // Nothing here logs the request body.
        [HttpPost("service-token")][AllowAnonymous]
        public ActionResult<TokenResult> ServiceToken(ServiceTokenRequestDto dto)
        {
            if (!_serviceClient.Matches(dto.ClientId, dto.ClientSecret))
                return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials.");

            return Ok(_tokens.CreateServiceToken(dto.ClientId));
        }
    }

    // Disallow: an unknown JSON property is a 400, matching additionalProperties: false in the contract.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ServiceTokenRequestDto([Required] string ClientId, [Required] string ClientSecret);

    public sealed record LoginDto([Required] string Identifier,[Required] string Password);
}


