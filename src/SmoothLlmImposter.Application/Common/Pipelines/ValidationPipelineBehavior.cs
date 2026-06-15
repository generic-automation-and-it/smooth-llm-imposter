using FluentValidation;
using FluentValidation.Results;
using Mediator;

namespace SmoothLlmImposter.Application.Common.Pipelines;

public sealed class ValidationPipelineBehavior<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        IValidator<TMessage>[] validatorArray = validators.ToArray();
        if (validatorArray.Length == 0)
        {
            return await next(message, cancellationToken);
        }

        var context = new ValidationContext<TMessage>(message);
        foreach (IValidator<TMessage> validator in validatorArray)
        {
            ValidationResult result = await validator.ValidateAsync(context, cancellationToken);
            if (!result.IsValid)
            {
                throw new ValidationException(result.Errors);
            }
        }

        return await next(message, cancellationToken);
    }
}
