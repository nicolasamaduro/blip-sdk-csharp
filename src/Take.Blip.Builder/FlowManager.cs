﻿using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lime.Messaging.Contents;
using Lime.Protocol;
using Take.Blip.Builder.Actions;
using Take.Blip.Builder.Hosting;
using Take.Blip.Builder.Models;
using Take.Blip.Client;
using Action = Take.Blip.Builder.Models.Action;

namespace Take.Blip.Builder
{
    public class FlowManager : IFlowManager
    {
        private readonly IConfiguration _configuration;
        private readonly IStorageManager _storageManager;
        private readonly IContextProvider _contextProvider;
        private readonly INamedSemaphore _namedSemaphore;
        private readonly IActionProvider _actionProvider;
        private readonly ISender _sender;

        public FlowManager(
            IConfiguration configuration,
            IStorageManager storageManager, 
            IContextProvider contextProvider, 
            INamedSemaphore namedSemaphore, 
            IActionProvider actionProvider,
            ISender sender)
        {
            _configuration = configuration;
            _storageManager = storageManager;
            _contextProvider = contextProvider;
            _namedSemaphore = namedSemaphore;
            _actionProvider = actionProvider;
            _sender = sender;
        }

        public async Task ProcessInputAsync(Document input, Identity user, Flow flow, CancellationToken cancellationToken)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (flow == null) throw new ArgumentNullException(nameof(flow));
            flow.Validate();

            var handle = await _namedSemaphore.WaitAsync($"{flow.Id}:{user}", _configuration.ExecutionSemaphoreExpiration, cancellationToken);
            try
            {
                // Try restore a stored state
                var stateId = await _storageManager.GetStateIdAsync(flow.Id, user, cancellationToken);
                var state = flow.States.FirstOrDefault(s => s.Id == stateId) ?? flow.States.Single(s => s.Root);

                // Load the user context
                var context = _contextProvider.GetContext(user, flow.Variables);

                do
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Validate the input for the current state
                    if (state.Input != null 
                        && !state.Input.Bypass 
                        && state.Input.Validation != null 
                        && !ValidateDocument(input, state.Input.Validation))
                    {
                        if (state.Input.Validation.Error != null)
                        {
                            // Send the validation error message
                            await _sender.SendMessageAsync(state.Input.Validation.Error, user.ToNode(),
                                cancellationToken);
                        }
                        break;
                    }

                    // Prepare to leave the current state executing the output actions
                    if (state.OutputActions != null)
                    {
                        await ProcessActionsAsync(context, state.OutputActions, cancellationToken);
                    }

                    // Determine the next state
                    state = await ProcessOutputsAsync(input, context, flow, state, cancellationToken);

                    await _storageManager.SetStateIdAsync(flow.Id, context.User, state?.Id, cancellationToken);

                    // Process the next state input actions
                    if (state?.InputActions != null)
                    {
                        await ProcessActionsAsync(context, state.InputActions, cancellationToken);

                    }
                } while (state != null && (state.Input == null || state.Input.Bypass));
            }
            finally
            {
                await handle.DisposeAsync();
            }
        }

        private bool ValidateDocument(Document input, InputValidation inputValidation)
        {
            switch (inputValidation.Rule)
            {
                case InputValidationRule.Text:
                    return input is PlainText;

                case InputValidationRule.Number:
                    return int.TryParse(input.ToString(), out _);

                case InputValidationRule.Date:
                    return DateTime.TryParse(input.ToString(), out _);

                case InputValidationRule.Regex:
                    return Regex.IsMatch(input.ToString(), inputValidation.Regex);

                case InputValidationRule.Type:
                    return input.GetMediaType() == inputValidation.Type;

                default:
                    throw new ArgumentOutOfRangeException(nameof(inputValidation));
            }
        }

        private async Task ProcessActionsAsync(IContext context, Action[] actions, CancellationToken cancellationToken)
        {
            // Execute all state actions
            foreach (var stateAction in actions.OrderBy(a => a.Order))
            {                
                var action = _actionProvider.Get(stateAction.Name);
                await action.ExecuteAsync(context, stateAction.Settings, cancellationToken);
            }            
        }

        private async Task<State> ProcessOutputsAsync(Document input, IContext context, Flow flow, State state, CancellationToken cancellationToken)
        {
            var outputs = state.Outputs;
            state = null;

            // If there's any output in the current state
            if (outputs != null)
            {
                // Evalute each output conditions
                foreach (var output in outputs.OrderBy(o => o.Order))
                {
                    var isValidOutput = true;

                    if (output.Conditions != null)
                    {
                        foreach (var outputCondition in output.Conditions)
                        {
                            isValidOutput = await EvaluateConditionAsync(outputCondition, input, context, cancellationToken);
                            if (!isValidOutput) break;
                        }
                    }

                    if (isValidOutput)
                    {
                        state = flow.States.SingleOrDefault(s => s.Id == output.StateId);
                        break;
                    }
                }
            }

            return state;
        }

        public async Task<bool> EvaluateConditionAsync(Condition condition, Document input, IContext context, CancellationToken cancellationToken)
        {
            string comparisonValue;
            if (condition.Variable == null)
            {
                comparisonValue = input.ToString();
            }
            else
            {
                comparisonValue = await context.GetVariableAsync(condition.Variable, cancellationToken);
            }

            switch (condition.Comparison)
            {
                case ConditionComparison.Equals:
                    return comparisonValue == condition.Value;

                case ConditionComparison.NotEquals:
                    return comparisonValue != condition.Value;

                case ConditionComparison.Contains:
                    return comparisonValue.Contains(condition.Value);

                case ConditionComparison.StartsWith:
                    return comparisonValue.StartsWith(condition.Value);

                case ConditionComparison.EndsWith:
                    return comparisonValue.EndsWith(condition.Value);

                case ConditionComparison.Matches:
                    return Regex.IsMatch(comparisonValue, condition.Value);

                default:
                    throw new ArgumentOutOfRangeException(nameof(condition));
            }
        }
    }
}