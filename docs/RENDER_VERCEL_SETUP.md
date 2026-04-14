# Render + Vercel Setup

Use this deployment model for the full system:

1. Backend on Render
2. Frontend on Vercel
3. Database on Supabase

## 1. Deploy the backend to Render

In Render:

1. Click **New**
2. Choose **Web Service**
3. Connect your GitHub repo
4. Select the `main` branch

Render should detect the included `render.yaml`.

If you enter settings manually, use:

- Runtime: `Docker / Native .NET Web Service` equivalent supported by Render
- Build Command:

```bash
dotnet publish "IMAJINATION BACKUP.csproj" -c Release -o out
```

- Start Command:

```bash
dotnet out/IMAJINATION BACKUP.dll
```

The app is already configured to bind to Render's `PORT`.

## 2. Add backend environment variables in Render

Open your Render service, then **Environment**.

Add these variables:

- `ConnectionStrings__SupabaseConnection`
- `EmailSettings__SmtpServer`
- `EmailSettings__Port`
- `EmailSettings__SenderName`
- `EmailSettings__SenderEmail`
- `EmailSettings__Username`
- `EmailSettings__Password`
- `PayMongo__SecretKey`
- `GoogleAuth__ClientId`

Also add your allowed origins:

- `AppSecurity__AllowedOrigins__0=http://localhost:5248`
- `AppSecurity__AllowedOrigins__1=https://localhost:5248`
- `AppSecurity__AllowedOrigins__2=https://YOUR-PROJECT.vercel.app`

After Vercel gives you a preview or production URL, use the real domain there.

## 3. Get your Render backend URL

After deploy, Render gives you a URL like:

```text
https://imajination-api.onrender.com
```

Keep that. Vercel will proxy `/api/*` to it.

## 4. Connect Vercel frontend to Render backend

Update `vercel.json` and place this rewrite at the top of the `rewrites` list:

```json
{
  "source": "/api/:path*",
  "destination": "https://YOUR-RENDER-URL.onrender.com/api/:path*"
}
```

Example:

```json
{
  "source": "/api/:path*",
  "destination": "https://imajination-api.onrender.com/api/:path*"
}
```

This uses Vercel's external rewrite support:

- Vercel rewrites: https://vercel.com/docs/rewrites

## 5. Deploy the frontend to Vercel

In Vercel:

1. Import the same GitHub repo
2. Framework Preset: `Other`
3. Build Command: leave empty
4. Output Directory: leave empty
5. Deploy

Because this frontend is static HTML, Vercel will serve it directly using `vercel.json`.

## 6. Test the full system

After both are deployed:

1. Open the Vercel URL
2. Try signup/login
3. Open events
4. Test booking/messages
5. Test ticket checkout
6. Test scanner

If API calls fail:

- verify the Render service is live
- verify `vercel.json` points to the exact Render URL
- verify the Vercel domain is included in `AppSecurity__AllowedOrigins`

## Notes

- Supabase remains your database. Do not move it into Render or Vercel.
- Vercel does not run this ASP.NET backend directly.
- Render environment variables can be added one by one or imported from a `.env` file.

Useful docs:

- Render environment variables: https://render.com/docs/configure-environment-variables
- Render default env vars including `PORT`: https://render.com/docs/environment-variables
- Vercel rewrites to external origins: https://vercel.com/docs/rewrites
