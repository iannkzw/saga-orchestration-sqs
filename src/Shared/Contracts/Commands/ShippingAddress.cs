namespace Shared.Contracts.Commands;

public record ShippingAddress(
    string Street,
    string City,
    string PostalCode,
    string Country
);
