﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Builder.Community.Adapters.Google.Integration;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace Bot.Builder.Community.Adapters.Google
{
    public class GoogleAdapter : BotAdapter
    {
        private Dictionary<string, List<Activity>> Responses { get; set; }

        private GoogleOptions Options { get; set; }

        public async Task<GoogleResponseBody> ProcessActivity(Payload actionPayload, GoogleOptions googleOptions, BotCallbackHandler callback)
        {
            TurnContext context = null;

            try
            {
                Options = googleOptions;

                var activity = RequestToActivity(actionPayload, googleOptions);
                BotAssert.ActivityNotNull(activity);

                context = new TurnContext(this, activity);

                Responses = new Dictionary<string, List<Activity>>();

                await base.RunPipelineAsync(context, callback, default(CancellationToken)).ConfigureAwait(false);

                var key = $"{activity.Conversation.Id}:{activity.Id}";

                try
                {
                    GoogleResponseBody response = null;
                    var activities = Responses.ContainsKey(key) ? Responses[key] : new List<Activity>();
                    response = CreateResponseFromLastActivity(activities, context);
                    return response;
                }
                finally
                {
                    if (Responses.ContainsKey(key))
                    {
                        Responses.Remove(key);
                    }
                }
            }
            catch (Exception ex)
            {
                await googleOptions.OnTurnError(context, ex);
                throw;
            }
        }

        public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, Activity[] activities, CancellationToken CancellationToken)
        {
            var resourceResponses = new List<ResourceResponse>();

            foreach (var activity in activities)
            {
                switch (activity.Type)
                {
                    case ActivityTypes.Message:
                    case ActivityTypes.EndOfConversation:
                        var conversation = activity.Conversation ?? new ConversationAccount();
                        var key = $"{conversation.Id}:{activity.ReplyToId}";

                        if (Responses.ContainsKey(key))
                        {
                            Responses[key].Add(activity);
                        }
                        else
                        {
                            Responses[key] = new List<Activity> { activity };
                        }

                        break;
                    default:
                        Trace.WriteLine(
                            $"GoogleAdapter.SendActivities(): Activities of type '{activity.Type}' aren't supported.");
                        break;
                }

                resourceResponses.Add(new ResourceResponse(activity.Id));
            }

            return Task.FromResult(resourceResponses.ToArray());
        }

        private static Activity RequestToActivity(Payload actionPayload, GoogleOptions googleOptions)
        {
            var activity = new Activity
            {
                ChannelId = "google",
                ServiceUrl = $"",
                Recipient = new ChannelAccount("", "action"),
                From = new ChannelAccount(actionPayload.User.UserId, "user"),

                Conversation = new ConversationAccount(false, "conversation",
                    $"{actionPayload.Conversation.ConversationId}"),

                Type = ActivityTypes.Message,
                Text = StripInvocation(actionPayload.Inputs[0]?.RawInputs[0]?.Query, googleOptions.ActionInvocationName),
                Id = new Guid().ToString(),
                Timestamp = DateTime.UtcNow,
                Locale = actionPayload.User.Locale
            };

            if(actionPayload.Inputs.FirstOrDefault()?.Arguments?.FirstOrDefault()?.Name == "OPTION")
            {
                activity.Text = actionPayload.Inputs.First().Arguments.First().TextValue;
            }

            activity.ChannelData = actionPayload;

            return activity;
        }

        private GoogleResponseBody CreateResponseFromLastActivity(IEnumerable<Activity> activities, ITurnContext context)
        {
            var activity = activities.Last();

            var response = new GoogleResponseBody()
            {
                Payload = new ResponsePayload()
                {
                    Google = new PayloadContent()
                    {
                        RichResponse = new RichResponse(),
                        ExpectUserResponse = Options.ShouldEndSessionByDefault ? false : true
                    }
                }
            };

            if (!string.IsNullOrEmpty(activity.Text))
            {
                var simpleResponse = new SimpleResponse
                {
                    Content = new SimpleResponseContent
                    {
                        DisplayText = activity.Text,
                        Ssml = activity.Speak,
                        TextToSpeech = activity.Text
                    }
                };

                var responseItems = new List<Item> { simpleResponse };

                // Add Google card to response if set
                AddCardToResponse(context, responseItems, activity);

                // Add Media response to response if set
                AddMediaResponseToResponse(context, responseItems, activity);

                response.Payload.Google.RichResponse.Items = responseItems.ToArray();

                // If suggested actions have been added to outgoing activity
                // add these to the response as Google Suggestion Chips
                AddSuggestionChipsToResponse(context, response);

                if(context.TurnState.ContainsKey("systemIntent"))
                {
                    var optionSystemIntent = context.TurnState.Get<ISystemIntent>("systemIntent");
                    response.Payload.Google.SystemIntent = optionSystemIntent;
                }

                // check if we should be listening for more input from the user
                switch (activity.InputHint)
                {
                    case InputHints.IgnoringInput:
                        response.Payload.Google.ExpectUserResponse = false;
                        break;
                    case InputHints.ExpectingInput:
                        response.Payload.Google.ExpectUserResponse = true;
                        break;
                    case InputHints.AcceptingInput:
                    default:
                        break;
                }
            }
            else
            {
                response.Payload.Google.ExpectUserResponse = false;
            }

            return response;
        }

        private void AddMediaResponseToResponse(ITurnContext context, List<Item> responseItems, Activity activity)
        {
            if (context.TurnState.ContainsKey("GoogleMediaResponse") && context.TurnState["GoogleMediaResponse"] is MediaResponse)
            {
                responseItems.Add(context.TurnState.Get<MediaResponse>("GoogleMediaResponse"));
            }
        }

        private static void AddSuggestionChipsToResponse(ITurnContext context, GoogleResponseBody response)
        {
            var suggestionChips = new List<Suggestion>();

            if (context.TurnState.ContainsKey("GoogleSuggestionChips") && context.TurnState["GoogleSuggestionChips"] is List<Suggestion>)
            {
                suggestionChips.AddRange(context.TurnState.Get<List<Suggestion>>("GoogleSuggestionChips"));
            }

            if (context.Activity.SuggestedActions != null && context.Activity.SuggestedActions.Actions.Any())
            {
                foreach (var suggestion in context.Activity.SuggestedActions.Actions)
                {
                    suggestionChips.Add(new Suggestion {Title = suggestion.Title});
                }
            }

            if (suggestionChips.Any())
            {
                response.Payload.Google.RichResponse.Suggestions = suggestionChips.ToArray();
            }
        }

        private void AddCardToResponse(ITurnContext context, List<Item> responseItems, Activity activity)
        {
            if (context.TurnState.ContainsKey("GoogleCard") && context.TurnState["GoogleCard"] is GoogleBasicCard)
            {
                responseItems.Add(context.TurnState.Get<GoogleBasicCard>("GoogleCard"));
            }
            else if (Options.TryConvertFirstActivityAttachmentTogoogleCard)
            {
                //TODO: Implement automatic conversion from hero card to Google basic card
                //CreateAlexaCardFromAttachment(activity, response);
            }
        }

        private static string StripInvocation(string query, string invocationName)
        {
            if (query.ToLower().StartsWith("talk to") || query.ToLower().StartsWith("speak to")
                                                      || query.ToLower().StartsWith("i want to speak to") ||
                                                      query.ToLower().StartsWith("ask"))
            {
                query = query.ToLower().Replace($"talk to", string.Empty);
                query = query.ToLower().Replace($"speak to", string.Empty);
                query = query.ToLower().Replace($"I want to speak to", string.Empty);
                query = query.ToLower().Replace($"ask", string.Empty);
            }

            query = query.TrimStart().TrimEnd();

            if (!string.IsNullOrEmpty(invocationName)
                && query.ToLower().StartsWith(invocationName.ToLower()))
            {
                query = query.ToLower().Replace(invocationName.ToLower(), string.Empty);
            }

            return query.TrimStart().TrimEnd();
        }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
