# Imajination Manuscript Alignment Checklist

This checklist compares the current manuscript claims against the implemented system in this repository.

Use it before defense and before final manuscript submission.

## Overall Verdict

The manuscript is mostly aligned with the current system.

Safe summary for defense:

"The manuscript is aligned with the implemented core system: role-based access, artist/sessionist booking, event posting, ticket purchasing, QR-based validation, messaging, notifications, and organizer/admin dashboards. Some technical descriptions were refined to match the final implementation more precisely."

## Keep These Claims

These are supported by the current codebase and are safe to defend.

- The system is a web-based indie gig management and artist booking platform.
- The platform supports multiple user roles: customer, organizer, artist, sessionist, and admin.
- The system centralizes gig listings, booking workflows, ticket purchasing, and dashboard monitoring.
- Artists and sessionists have profile pages and can be discovered and booked through the platform.
- Organizers can create and manage events.
- The platform supports booking requests and booking status tracking.
- The platform includes a built-in contract/terms workflow for bookings.
- The platform supports messaging between booking participants.
- The platform includes notification features for booking and payment updates.
- The platform supports PayMongo-based online payments.
- The system generates QR-based tickets and supports QR scanning for validation.
- The platform includes organizer/admin analytics and reporting views.
- The backend uses ASP.NET Core on .NET 9.
- The system uses PostgreSQL through Npgsql and is configured around a Supabase connection string.
- Password hashing with BCrypt is correctly stated.
- The frontend uses HTML, CSS, JavaScript, Tailwind CSS, Lucide, and Chart.js.
- Google sign-in support is present.

## Revise These Claims

These are close to true, but the wording should be updated so the manuscript matches the actual implementation exactly.

- Revise: "The system implements JWT Bearer Authentication."
  Use instead: "The system uses a hybrid authentication approach built around JWT and session-token validation, with support for multi-factor authentication."

- Revise: "Email notifications, including booking confirmations and digital receipts, are handled by Brevo."
  Use instead: "The system uses SMTP-based email for account and administrative email flows, while booking, payment, and receipt updates are primarily tracked through in-system records and notifications."

- Revise: "Audiences receive digital receipts as proof of payment."
  Use instead: "The system records payment and receipt status digitally and exposes receipt-related information through the platform."

- Revise: "The platform includes digital contract confirmation."
  Use instead: "The platform includes a booking contract module where terms, fee details, and contract status can be created, updated, and acknowledged inside the system."

- Revise: "Ticket validation is fully integrated into the platform."
  Use instead: "The platform supports QR-based ticket validation through the organizer scanner workflow."

- Revise: "The platform supports secure user authentication."
  Use instead: "The platform supports role-based authentication with password hashing, Google sign-in, token/session handling, and optional MFA."

## Avoid Saying These As-Is

These are the claims most likely to get challenged if a panel asks for a live demonstration.

- Avoid saying that email receipts are already fully sent to users after every payment unless you can demo that exact flow.
- Avoid saying Brevo powers all booking and payment notifications.
- Avoid saying the system uses only JWT authentication.
- Avoid saying all ticket validation is fully automated by dedicated hardware.
- Avoid saying the platform guarantees artist quality, professionalism, or booking outcomes.
- Avoid implying that every described feature is already production-hardened if it is still under active refinement.

## Strong Defense Wording

If the panel asks whether the paper matches the actual system, use this:

"Yes. The manuscript matches the implemented core workflow of the system: role-based registration and login, event posting, artist and sessionist discovery, booking requests, contract handling, messaging, ticket purchases, QR validation, and dashboard monitoring. We refined a few technical descriptions to better match the final implementation, especially around authentication and how receipt and notification flows are handled."

If the panel asks about security, use this:

"The system uses hashed passwords, token and session-based authentication, rate limiting, security headers, and optional MFA. We also implemented role-based access handling and protected several transaction-related flows."

If the panel asks about receipts or email updates, use this:

"The system keeps digital payment and receipt records inside the platform. Some email-based account flows are already implemented, and we describe receipt and payment confirmation primarily as system-tracked records and notifications."

## Technical Mapping

Use these code references if you need to justify manuscript claims.

- Authentication and Google sign-in: [Controllers/AuthController.cs](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/Controllers/AuthController.cs)
- Hybrid auth, security headers, rate limiting: [Program.cs](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/Program.cs)
- Session-token authentication: [Services/SessionTokenAuthenticationHandler.cs](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/Services/SessionTokenAuthenticationHandler.cs)
- Booking workflow and contracts: [Controllers/BookingController.cs](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/Controllers/BookingController.cs)
- Ticket checkout, QR scan, refunds: [Controllers/TicketController.cs](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/Controllers/TicketController.cs)
- Event creation and analytics support: [Controllers/EventController.cs](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/Controllers/EventController.cs)
- Messaging: [Controllers/MessageController.cs](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/Controllers/MessageController.cs)
- Organizer analytics dashboard: [pages/dashboards/OrganizerDashboard.html](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/pages/dashboards/OrganizerDashboard.html)
- Admin analytics dashboard: [pages/dashboards/ProfileAdmin.html](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/pages/dashboards/ProfileAdmin.html)
- QR scanner page: [pages/tools/dashboardscanner.html](/c:/Users/Aspect/OneDrive/Documents/IMAJINATION%20BACKUP%20ORIGINAL/pages/tools/dashboardscanner.html)

## Quick Manuscript Fixes

These are the highest-value edits to make in the paper before final defense or submission.

1. Update the authentication description from "JWT only" to "hybrid authentication with MFA support."
2. Soften the Brevo/email receipt claim unless you can demonstrate full receipt emailing.
3. Keep "digital receipts" framed as system-tracked payment/receipt records unless you want to implement and demo email receipt delivery.
4. Standardize the title wording everywhere. Use one exact title only.
5. Clean up grammar in the objectives and scope sections.

## Language Cleanup Notes

These should be fixed in the manuscript text itself.

- Keep "Developed for Imajin Arts & Music" or "Developed for Imajin Arts & Music in Metro Manila" consistent everywhere.
- Fix spacing and punctuation issues like "artists'booking".
- Rewrite awkward lines like "generates digital receipts requires payment confirmation".
- Check capitalization consistency for role names, system names, and headings.

## Final Recommendation

The manuscript is good enough in substance.

What it needs is precision, not a rewrite.

If you align the technical wording with the implementation and remove a few overstated claims, it will be much safer in defense.
