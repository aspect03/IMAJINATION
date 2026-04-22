# Security And Quality Baseline

This project includes a concrete implementation baseline that supports OWASP-oriented hardening and ISO/IEC 25010 quality work without claiming formal certification.

## Implemented Security Controls

- JWT validation with issuer, audience, and expiry controls
- Session tracking with server-side revocation support
- Stronger session token generation using cryptographic randomness
- CSRF protection for state-changing API requests
- Global rate limiting with stricter auth-route limits
- Security headers including CSP, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, and `Permissions-Policy`
- Non-cacheable API responses for sensitive data flows
- Password hashing with BCrypt
- MFA enrollment and verification with TOTP
- Security audit logging and login/OTP throttling
- Parameterized SQL throughout the data-access layer
- Safer production error handling for auth and payment flows

## ISO 25010 Alignment

The codebase currently supports these quality characteristics:

- Functional suitability: protected checkout, booking, ticketing, and MFA flows
- Security: authentication, authorization, CSRF, rate limiting, audit logging, and safer error handling
- Reliability: global exception handling and tracked session revocation
- Maintainability: centralized configuration fallbacks and shared security helpers
- Usability: password-strength guidance, MFA setup flow, and clearer error messages

## Important Boundary

This file does not mean the system is formally OWASP-certified or ISO/IEC 25010-certified.

To make a stronger external claim, the project still needs:

- documented secure SDLC and threat modeling
- automated security testing in CI
- dependency and SCA monitoring
- formal quality metrics and acceptance thresholds
- repeatable performance, compatibility, and recovery testing
