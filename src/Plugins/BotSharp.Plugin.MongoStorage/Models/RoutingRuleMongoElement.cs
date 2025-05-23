using BotSharp.Abstraction.Routing.Models;

namespace BotSharp.Plugin.MongoStorage.Models;

[BsonIgnoreExtraElements(Inherited = true)]
public class RoutingRuleMongoElement
{
    public string Field { get; set; } = default!;
    public string Description { get; set; } = default!;
    public bool Required { get; set; }
    public string? RedirectTo { get; set; }
    public string Type { get; set; } = default!;
    public string FieldType { get; set; } = default!;

    public static RoutingRuleMongoElement ToMongoElement(RoutingRule routingRule)
    {
        return new RoutingRuleMongoElement 
        {
            Field = routingRule.Field,
            Description = routingRule.Description,
            Required = routingRule.Required,
            RedirectTo = routingRule.RedirectTo,
            Type = routingRule.Type,
            FieldType = routingRule.FieldType
        };
    }

    public static RoutingRule ToDomainElement(string agentId, string agentName, RoutingRuleMongoElement rule)
    {
        return new RoutingRule
        {
            AgentId = agentId,
            AgentName = agentName,
            Field = rule.Field,
            Description = rule.Description,
            Required = rule.Required,
            RedirectTo = rule.RedirectTo,
            Type = rule.Type,
            FieldType= rule.FieldType
        };
    }

    public override string ToString()
    {
        return $"{Field} - {FieldType}, Required: {Required} ({Type})";
    }
}
