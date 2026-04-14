# Vercel Deployment

This repository is an ASP.NET Core application with server controllers under `/Controllers` and static HTML under `/pages`.

Vercel can host the static frontend, but it cannot run this ASP.NET Core backend as-is.

## Required deployment model

Use:

1. Vercel for the frontend
2. A separate host for the ASP.NET API

Suggested backend hosts:

- Render
- Railway
- Azure App Service
- VPS / Docker host

## Frontend on Vercel

The repo includes `vercel.json` so Vercel can serve the static HTML pages correctly.

When importing this repo into Vercel:

1. Import the GitHub repository
2. Framework preset: `Other`
3. Build command: leave empty
4. Output directory: leave empty
5. Deploy

## Backend host

Publish the ASP.NET app separately:

```powershell
dotnet publish -c Release
```

Deploy the published output to your backend host.

## API routing after frontend deploy

Your frontend currently calls relative paths like:

- `/api/auth/...`
- `/api/event/...`
- `/api/ticket/...`

That means one of these must be done:

1. Put the frontend behind a reverse proxy that forwards `/api/*` to your ASP.NET backend
2. Add a Vercel rewrite that proxies `/api/*` to your backend URL
3. Rewrite the frontend code to use an absolute API base URL

## Vercel rewrite to backend

After you know your backend URL, add this to `vercel.json`:

```json
{
  "rewrites": [
    { "source": "/api/:path*", "destination": "https://YOUR-BACKEND-DOMAIN/api/:path*" }
  ]
}
```

Put that rewrite at the top of the `rewrites` array.

## CORS

Your backend must allow your Vercel frontend origin.

Add your Vercel domain to `.env` or app settings:

```env
AppSecurity__AllowedOrigins__3=https://YOUR-PROJECT.vercel.app
```

If you use a custom Vercel domain, add that too.

## Environment variables required on backend

Your backend host needs these values:

- `ConnectionStrings__SupabaseConnection`
- `EmailSettings__SmtpServer`
- `EmailSettings__Port`
- `EmailSettings__SenderName`
- `EmailSettings__SenderEmail`
- `EmailSettings__Username`
- `EmailSettings__Password`
- `PayMongo__SecretKey`
- `AppSecurity__AllowedOrigins__0..n`

## Important limitation

If you deploy only to Vercel without a backend host, login, payments, bookings, notifications, scanner, email, and database-backed features will fail.
