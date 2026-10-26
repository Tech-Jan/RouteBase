namespace RouteBaseWeb.Models.Requests;

/// <summary>Body for POST api/friends/request — the id of the user to befriend.</summary>
public class SendRequestBody
{
    public string AddresseeId { get; set; } = string.Empty;
}
