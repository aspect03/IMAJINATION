# Panel Q&A Cheat Sheet

Use this sheet for short, defense-safe answers during the panel presentation.

## Core Alignment

Question:
"Does the manuscript still match the actual system?"

Answer:
"Yes. The manuscript matches the implemented core workflow of the system: role-based registration and login, event posting, artist and sessionist discovery, booking requests, contract handling, messaging, ticket purchases, QR validation, and dashboard monitoring. We only refined a few technical descriptions to better match the final implementation."

## System Flow

Question:
"What is the overall flow of the system?"

Answer:
"Users first register and log in based on their role. Organizers can create and manage events, artists and sessionists can build bookable profiles, customers can browse events and talent, send booking requests, purchase tickets, and receive QR-based ticket access. The system also supports messaging, notifications, dashboards, and administrative monitoring."

## Why The System Is Needed

Question:
"What problem does the system solve?"

Answer:
"The system solves the fragmented process of indie gig management. Instead of relying on separate social media messages, spreadsheets, and manual payment tracking, the platform centralizes event posting, talent booking, ticketing, and monitoring into one web-based system."

## User Roles

Question:
"What user roles are supported?"

Answer:
"The platform supports customer, organizer, artist, sessionist, and admin roles. Each role has its own access level and dashboard based on its responsibilities in the system."

## Booking Flow

Question:
"How does booking work in the platform?"

Answer:
"Customers or organizers can request bookings from artists or sessionists through the platform. Booking records, status updates, payment-related states, contract details, and related communication are tracked inside the system."

## Contracts

Question:
"Do you support contracts?"

Answer:
"Yes. The platform includes a booking contract and terms workflow where terms, fees, and agreement status can be created, updated, and acknowledged inside the system."

## Ticketing

Question:
"How does ticketing work?"

Answer:
"Customers can browse events, select tickets, complete payment, and receive QR-based digital tickets. Organizers can then validate those tickets through the scanner workflow."

## QR Validation

Question:
"Is ticket validation implemented?"

Answer:
"Yes. The platform supports QR-based ticket validation through the organizer scanner workflow."

Safer wording:
"The platform supports QR-based ticket validation through the system scanner page."

Avoid saying:
"It is fully hardware-automated."

## Authentication

Question:
"What kind of authentication does the system use?"

Answer:
"The system uses a hybrid authentication approach with password-based login, token and session validation, Google sign-in support, and optional multi-factor authentication."

Avoid saying:
"The system uses JWT only."

## Security

Question:
"How is the system secured?"

Answer:
"The system uses password hashing with BCrypt, hybrid authentication, role-based access control, CSRF protection, rate limiting, login lockout, OTP throttling, MFA support, audit logging, upload validation, security headers, and session revocation."

Safer follow-up:
"We describe the system as security-hardened, not as formally certified."

## Payments

Question:
"How are payments handled?"

Answer:
"The platform supports PayMongo-based payment processing for relevant ticketing and transaction flows, while payment records and statuses are tracked inside the system."

## Receipts

Question:
"Do users receive digital receipts?"

Answer:
"The system maintains digital payment and receipt-related records inside the platform and exposes payment confirmation information through user-facing flows."

Avoid saying:
"Every payment automatically sends a full receipt email," unless that exact flow is being demonstrated live.

## Email And Notifications

Question:
"Does the system send email notifications?"

Answer:
"The system already supports SMTP-based email for account-related flows, while booking, payment, and activity updates are mainly handled through in-system notifications and records."

Avoid saying:
"Brevo handles all booking and payment notifications."

## Manuscript Drift

Question:
"Were there any changes from the manuscript?"

Answer:
"There were no major changes to the core workflow. What changed mainly were technical descriptions, so we refined some wording to better match the final implementation, especially in authentication and receipt or notification handling."

## Scope Limits

Question:
"What are the system limitations?"

Answer:
"The system is focused on web-based indie gig management within the project scope. It does not guarantee performer quality or booking outcomes, and some production-level enhancements such as broader automated testing and further hardening can still be extended in future work."

## Best Closing Line

If the panel asks for a short final summary, say:

"Imajination is a role-based web platform that centralizes indie gig management, artist and sessionist booking, ticket purchasing, QR validation, messaging, notifications, and dashboard monitoring. The final system remains aligned with the manuscript’s core objectives and workflow."
