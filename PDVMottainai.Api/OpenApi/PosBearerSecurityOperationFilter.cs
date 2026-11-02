using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using PDVMottainai.Api.Endpoints;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PDVMottainai.Api.OpenApi;

public sealed class PosBearerSecurityOperationFilter : IOperationFilter
{
    public const string PosBearerScheme = "PosBearer";
    public const string FirebaseBearerScheme = "FirebaseBearer";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var method = context.MethodInfo;
        var controller = method.DeclaringType;
        var allowsAnonymous = method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Length > 0
            || (controller is not null
                && controller.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Length > 0);

        var authorizeAttributes = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Concat(controller is null
                ? []
                : controller.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                    .Cast<AuthorizeAttribute>())
            .ToArray();

        string? scheme = null;
        if (controller == typeof(AuthController) && method.Name == nameof(AuthController.Login))
        {
            scheme = FirebaseBearerScheme;
        }
        else if (!allowsAnonymous && authorizeAttributes.Length > 0)
        {
            var usesFirebase = authorizeAttributes
                .SelectMany(attribute => (attribute.AuthenticationSchemes ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Any(authenticationScheme => string.Equals(
                    authenticationScheme, "Firebase", StringComparison.OrdinalIgnoreCase));
            scheme = usesFirebase ? FirebaseBearerScheme : PosBearerScheme;
        }

        if (scheme is null)
            return;

        operation.Security =
        [
            .. (operation.Security ?? []),
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(scheme, context.Document)] = []
            }
        ];
    }
}
