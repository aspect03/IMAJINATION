using System;

namespace ImajinationAPI.Models
{
    // Used for the Login endpoint
    public class LoginDto 
    { 
        public string email { get; set; } 
        public string password { get; set; } 
    }

    // Used to trigger the Email OTP sending
    public class SendOtpDto 
    {
        public string email { get; set; }
    }

    // Used for the final Registration insert
    public class RegisterDto 
    {
        public string role { get; set; }
        public string firstName { get; set; }
        public string? middleName { get; set; } 
        public string lastName { get; set; }
        public string? suffix { get; set; } 
        public string username { get; set; }
        public string email { get; set; }
        public string contactNumber { get; set; }
        public string address { get; set; }
        public DateTime birthday { get; set; }
        public int age { get; set; }
        public string password { get; set; } 
        
        // Nullable fields for specific roles
        public string? stageName { get; set; }      
        public string? productionName { get; set; } 
        public string? talentCategory { get; set; }
        public string? memberNames { get; set; }
        
        // The 6-digit verification code
        public string? otp { get; set; } 
    } // <-- I moved this closing brace UP here!

    // Used for the Forgot Password flow
    public class ResetPasswordDto
    {
        public string email { get; set; }
        public string otp { get; set; }
        public string newPassword { get; set; }
    }

    public class GoogleLoginDto
    {
        public string credential { get; set; }
    }
}
