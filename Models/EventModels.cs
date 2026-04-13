using System;
using System.Collections.Generic;

namespace ImajinationAPI.Models
{
    public class TalentLineupItemDto
    {
        public Guid id { get; set; }
        public string displayName { get; set; } = "";
        public string role { get; set; } = "";
        public string? profilePicture { get; set; }
    }

    public class CreateEventDto
    {
        public Guid organizerId { get; set; }
        public string title { get; set; }
        public string artists { get; set; }
        public string description { get; set; }
        public DateTime time { get; set; }
        
        // NEW: Separate City and Location fields!
        public string city { get; set; } 
        public string location { get; set; }
        
        public string? posterUrl { get; set; }
        public decimal price { get; set; }
        public int slots { get; set; }
        
        public string? eventType { get; set; } 
        public string? genres { get; set; }

        public string? tierName { get; set; }
        public decimal? tierPrice { get; set; }
        public int? tierSlots { get; set; }
        public string? bundles { get; set; }
        public string? discounts { get; set; }
        public string? sponsors { get; set; }
        public string? saleName { get; set; }
        public string? saleType { get; set; }
        public decimal? saleValue { get; set; }
        public DateTime? saleStartsAt { get; set; }
        public DateTime? saleEndsAt { get; set; }
        public List<TalentLineupItemDto>? artistLineup { get; set; }
        public List<TalentLineupItemDto>? sessionistLineup { get; set; }
    }

    public class EventDto
    {
        public Guid id { get; set; }
        public string title { get; set; }
        public DateTime time { get; set; }
        
        // NEW: Separate City and Location fields!
        public string city { get; set; }
        public string location { get; set; }
        
        public decimal price { get; set; }
        public int slots { get; set; }
        public int ticketsSold { get; set; }
        public int attendedTickets { get; set; }
        public string status { get; set; }
        public string? posterUrl { get; set; } 
        
        public string? eventType { get; set; }
        public string? genres { get; set; }
        public string? saleName { get; set; }
        public string? saleType { get; set; }
        public decimal? saleValue { get; set; }
        public DateTime? saleStartsAt { get; set; }
        public DateTime? saleEndsAt { get; set; }
        public List<TalentLineupItemDto> artistLineup { get; set; } = new();
        public List<TalentLineupItemDto> sessionistLineup { get; set; } = new();
    }
}
