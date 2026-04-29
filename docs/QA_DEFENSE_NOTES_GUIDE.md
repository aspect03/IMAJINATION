# QA Defense Notes Guide

Use this guide if you are the quality assurance presenter during the defense.

## Main QA Role

Say this:

"As the quality assurance representative, my role is to verify that the system works according to the intended requirements, that the major features behave correctly for each user role, and that identified issues are documented, retested, and resolved."

Shorter version:

"My role in QA is to test the system, validate the expected results, document issues, and confirm that the implemented system matches the required workflow."

## How To Explain The System As QA

Do not explain the project like a developer who built every module line by line.

Explain it like this:

"From a QA perspective, we tested the system as a role-based web platform for indie gig management. We verified the core workflows such as registration and login, role-based access, profile management, event posting, artist and sessionist discovery, booking requests, contract handling, ticket purchasing, QR validation, messaging, notifications, and dashboard monitoring."

Follow-up line:

"We focused on whether the features worked correctly, whether users saw the correct results, whether invalid input was rejected properly, and whether role restrictions were enforced."

## Safe Defense Flow

When answering, use this 4-part pattern:

1. What feature was tested
2. What QA checked
3. What expected result should happen
4. What actual result was observed

Example:

"For customer profile management, we tested whether a customer could view and update profile details correctly. We checked required fields, email formatting, username uniqueness, password validation, and error handling. The expected result was that valid updates would be saved and invalid entries would be rejected with proper messages. During testing, the system behaved as expected and enforced those validations."

## Note-Taking Format

Use this simple format while reviewing or while preparing for the panel:

- `Feature`
- `Purpose`
- `QA checks performed`
- `Expected result`
- `Actual result`
- `Issue found`
- `Status`

## Sample QA Notes For This System

### 1. Login And Authentication

- `Feature:` Login and authentication
- `Purpose:` Allows users to access the platform based on their role
- `QA checks performed:` Valid login, invalid login, role-based access, session/token handling, Google sign-in support, MFA-related flow
- `Expected result:` Authorized users can log in and access correct pages; invalid attempts are blocked
- `Actual result:` Core authentication flow works and role restrictions are enforced
- `Issue found:` Note any failed redirect, wrong role access, or invalid session handling
- `Status:` Passed / revise if needed

### 2. Customer Profile Management

- `Feature:` Customer profile update
- `Purpose:` Lets customers manage personal account details
- `QA checks performed:` Required first name and last name, required username, valid email, unique email, unique username, password change validation, current password verification, image validation
- `Expected result:` Valid data is saved; duplicate or invalid input is rejected with clear messages
- `Actual result:` The profile update flow validates required fields and blocks invalid or unsafe input
- `Issue found:` Note any mismatch in saved data, validation, or returned message
- `Status:` Passed / revise if needed

### 3. Event Browsing And Discovery

- `Feature:` Event and talent discovery
- `Purpose:` Lets users find events, artists, and sessionists
- `QA checks performed:` Correct list display, correct filtering, visible profile information, role-appropriate access
- `Expected result:` Users can browse and view relevant content without unauthorized actions
- `Actual result:` Discovery flows are accessible according to role and intended workflow
- `Issue found:` Note missing data, broken filters, or incorrect access
- `Status:` Passed / revise if needed

### 4. Booking Workflow

- `Feature:` Booking request process
- `Purpose:` Supports booking between customers or organizers and artists or sessionists
- `QA checks performed:` Request creation, status changes, contract-related states, visible updates, record tracking
- `Expected result:` Booking actions are recorded correctly and statuses update as expected
- `Actual result:` Core booking flow is trackable inside the platform
- `Issue found:` Note incorrect status, duplicate action, or missing record
- `Status:` Passed / revise if needed

### 5. Ticketing And QR Validation

- `Feature:` Ticket purchase and QR validation
- `Purpose:` Allows customers to buy tickets and organizers to validate them
- `QA checks performed:` Ticket purchase flow, payment status tracking, QR generation, QR scan validation
- `Expected result:` Valid purchases generate usable ticket records and valid QR-based access
- `Actual result:` Ticketing and scanner workflow support event access validation
- `Issue found:` Note payment mismatch, invalid QR handling, or duplicate scan concern
- `Status:` Passed / revise if needed

## Good QA Phrases During Defense

Use these lines naturally:

"In QA, we validated whether the feature met the expected behavior."

"We checked both successful and invalid input scenarios."

"We documented observed issues, then retested after fixes were applied."

"Our focus was on functionality, validation, access control, and consistency with the intended workflow."

"From the QA side, the implemented system remained aligned with the documented core process."

## If The Panel Asks What QA Actually Did

Answer:

"We performed functional checking of the major modules, reviewed user-role behavior, tested expected and invalid inputs, observed system responses, documented issues, and confirmed whether corrections worked after retesting."

Safer follow-up:

"Since this is within the academic project scope, our QA focus was on system behavior, validation, and workflow consistency rather than formal enterprise certification."

## If The Panel Asks For Limitations

Answer:

"From a QA perspective, the system was validated within the project scope and available test scenarios. Like most academic systems, it can still be extended with broader automated testing, larger-scale user testing, and additional production hardening in future work."

## Best Closing Line

"As the QA representative, I can say that the system's core features were checked against the intended workflow, the major user-facing processes behaved as expected within scope, and the final implementation remained aligned with the project's objectives."
