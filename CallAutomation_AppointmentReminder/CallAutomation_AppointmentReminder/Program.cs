using Azure;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using CallAutomation_AppointmentReminder;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//Fetch configuration and add call automation as singleton service
var callConfigurationSection = builder.Configuration.GetSection(nameof(CallConfiguration));
builder.Services.Configure<CallConfiguration>(callConfigurationSection);
builder.Services.AddSingleton(new CallAutomationClient(callConfigurationSection["ConnectionString"]));

var app = builder.Build();
var TargetIdentity = "";
var sourceIdentity = await app.ProvisionAzureCommunicationServicesIdentity(callConfigurationSection["ConnectionString"]);
var target = callConfigurationSection["AddParticipantNumber"];
var addedParticipants = target.Split(';');
int addedParticipantsCount = 0;
int declineParticipantsCount = 0;
var SourceId = "";

CommunicationIdentifierKind GetIdentifierKind(string participantnumber)
{
    //checks the identity type returns as string
    return Regex.Match(participantnumber, Constants.userIdentityRegex, RegexOptions.IgnoreCase).Success ? CommunicationIdentifierKind.UserIdentity :
          Regex.Match(participantnumber, Constants.phoneIdentityRegex, RegexOptions.IgnoreCase).Success ? CommunicationIdentifierKind.PhoneIdentity :
          CommunicationIdentifierKind.UnknownIdentity;
}

// Api to initiate out bound call
app.MapPost("/api/call", async ([Required] string targetNo, CallAutomationClient callAutomationClient, IOptions<CallConfiguration> callConfiguration, ILogger<Program> logger) =>
{

    var acsAcquiredNumber = new PhoneNumberIdentifier(callConfiguration.Value.SourcePhoneNumber);
    if (!string.IsNullOrEmpty(targetNo))
    {
        var identities = targetNo.Split(';');
        foreach (var target in identities)
        {
            if (!string.IsNullOrEmpty(target))
            {
                TargetIdentity = target;
                CallInvite? callInvite = null;
                var identifierKind = GetIdentifierKind(target);

                if (identifierKind == CommunicationIdentifierKind.PhoneIdentity)
                {
                    callInvite = new CallInvite(new PhoneNumberIdentifier(target), acsAcquiredNumber);
                }
                else if (identifierKind == CommunicationIdentifierKind.UserIdentity)
                {
                    callInvite = new CallInvite(new CommunicationUserIdentifier(target));
                }

                var createCallOption = new CreateCallOptions(callInvite, new Uri(callConfiguration.Value.CallbackEventUri));
                var response = await callAutomationClient.CreateCallAsync(createCallOption).ConfigureAwait(false);
                logger.LogInformation($"Reponse from create call: {response.GetRawResponse()}" +
                    $"CallConnection Id : {response.Value.CallConnection.CallConnectionId}");
                SourceId = response.Value.CallConnectionProperties.Source.RawId;
            }
        }
    }
    else
    {
        TargetIdentity = callConfiguration.Value.TargetPhoneNumber;
        if (!string.IsNullOrEmpty(TargetIdentity))
        {
            var identifierKind = GetIdentifierKind(TargetIdentity);
            CallInvite? callInvite = null;
            if (identifierKind == CommunicationIdentifierKind.PhoneIdentity)
            {
                callInvite = new CallInvite(new PhoneNumberIdentifier(TargetIdentity), acsAcquiredNumber);
            }

            else if (identifierKind == CommunicationIdentifierKind.UserIdentity)
            {
                callInvite = new CallInvite(new CommunicationUserIdentifier(TargetIdentity));
            }
            var createCallOption = new CreateCallOptions(callInvite, new Uri(callConfiguration.Value.CallbackEventUri));
            var response = await callAutomationClient.CreateCallAsync(createCallOption).ConfigureAwait(false);
            logger.LogInformation($"Reponse from create call: {response.GetRawResponse()}" +
                $"CallConnection Id : {response.Value.CallConnection.CallConnectionId}");
        }
    }
});

//api to handle call back events
app.MapPost("/api/callbacks", async (CloudEvent[] cloudEvents, CallAutomationClient callAutomationClient, IOptions<CallConfiguration> callConfiguration, ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        logger.LogInformation($"Event received: {JsonConvert.SerializeObject(cloudEvent)}");

        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        var callConnection = callAutomationClient.GetCallConnection(@event.CallConnectionId);
        var callConnectionMedia = callConnection.GetCallMedia();
        if (@event is CallConnected)
        {
            addedParticipantsCount = 0;
            declineParticipantsCount = 0;
            //Initiate recognition as call connected event is received
            logger.LogInformation($"CallConnected event received for call connection id: {@event.CallConnectionId}" + $" Correlation id: {@event.CorrelationId}");

            var properties = callConnection.GetCallConnectionProperties();
            logger.LogInformation($"call connection properties -------> SourceIdentity : {properties.Value.Source.RawId}," +
                $"CallConnection State : {properties.Value.CallConnectionState}");
            logger.LogInformation($"targets ------->");
            foreach (var target in properties.Value.Targets)
            {
                logger.LogInformation($"{target.RawId}");
            }

            var recognizeOptions =
            new CallMediaRecognizeDtmfOptions(CommunicationIdentifier.FromRawId(TargetIdentity), maxTonesToCollect: 1)
            {
                InterruptPrompt = true,
                InterToneTimeout = TimeSpan.FromSeconds(10),
                InitialSilenceTimeout = TimeSpan.FromSeconds(5),
                Prompt = new FileSource(new Uri(callConfiguration.Value.AppBaseUri + callConfiguration.Value.AppointmentReminderMenuAudio)),
                OperationContext = "AppointmentReminderMenu"
            };

            //Start recognition 
            await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
        }
        if (@event is RecognizeCompleted { OperationContext: "AppointmentReminderMenu" })
        {
            // Play audio once recognition is completed sucessfully
            logger.LogInformation($"RecognizeCompleted event received for call connection id: {@event.CallConnectionId}" + $" Correlation id: {@event.CorrelationId}");
            var recognizeCompletedEvent = (RecognizeCompleted)@event;
            DtmfTone toneDetected = ((DtmfResult)recognizeCompletedEvent.RecognizeResult).Tones[0];
            if (toneDetected == DtmfTone.Three)
            {
                var playSource = new List<PlaySource> { Utils.GetAudioForTone(toneDetected, callConfiguration) };
                // Play audio for dtmf response
                await callConnectionMedia.PlayToAllAsync(new PlayToAllOptions(playSource) { OperationContext = "AgentConnect", Loop = false });

            }
            else
            {
                var playSource = Utils.GetAudioForTone(toneDetected, callConfiguration);
                // Play audio for dtmf response
                await callConnectionMedia.PlayToAllAsync(new PlayToAllOptions((IEnumerable<PlaySource>)playSource) { OperationContext = "ResponseToDtmf", Loop = false });
            }
        }
        if (@event is PlayCompleted { OperationContext: "AgentConnect" })
        {
            foreach (var Participantindentity in addedParticipants)
            {
                CallInvite? callInvite = null;
                if (!string.IsNullOrEmpty(Participantindentity))
                {
                    var identifierKind = GetIdentifierKind(Participantindentity.Trim());
                    if (identifierKind == CommunicationIdentifierKind.PhoneIdentity)
                    {
                        callInvite = new CallInvite(new PhoneNumberIdentifier(Participantindentity.Trim()), new PhoneNumberIdentifier(callConfiguration.Value.SourcePhoneNumber));
                    }

                    else if (identifierKind == CommunicationIdentifierKind.UserIdentity)
                    {
                        callInvite = new CallInvite(new CommunicationUserIdentifier(Participantindentity.Trim()));
                    }
                    var addParticipantOptions = new AddParticipantOptions(callInvite);
                    var response = await callConnection.AddParticipantAsync(addParticipantOptions);
                    var playSource = new FileSource(new Uri(callConfiguration.Value.AppBaseUri + callConfiguration.Value.AddParticipant));
                    await callConnectionMedia.PlayToAllAsync(new PlayToAllOptions((IEnumerable<PlaySource>)playSource) { OperationContext = "addParticipant", Loop = false });
                    await Task.Delay(TimeSpan.FromSeconds(10));

                    logger.LogInformation($"Add participant call : {response.Value.Participant}" + $"  Status of call :{response.GetRawResponse().Status}"
                        + $"  participant ID: {response.Value.Participant.Identifier}" + $" participat is muted : {response.Value.Participant.IsMuted}");
                }
            }
        }

        if (@event is RecognizeFailed { OperationContext: "AppointmentReminderMenu" })
        {
            logger.LogInformation($"RecognizeFailed event received for call connection id: {@event.CallConnectionId}" + $" Correlation id: {@event.CorrelationId}");
            var recognizeFailedEvent = (RecognizeFailed)@event;

            // Check for time out, and then play audio message
            if (recognizeFailedEvent.ReasonCode.Equals(MediaEventReasonCode.RecognizeInitialSilenceTimedOut))
            {
                logger.LogInformation($"Recognition timed out for call connection id: {@event.CallConnectionId}" + $" Correlation id: {@event.CorrelationId}");
                var playSource = new FileSource(new Uri(callConfiguration.Value.AppBaseUri + callConfiguration.Value.TimedoutAudio));

                //Play audio for time out
                await callConnectionMedia.PlayToAllAsync(new PlayToAllOptions((IEnumerable<PlaySource>)playSource) { OperationContext = "ResponseToDtmf", Loop = false });
            }
        }

        if (@event is PlayCompleted { OperationContext: "ResponseToDtmf" })
        {
            logger.LogInformation($"PlayCompleted event received for call connection id: {@event.CallConnectionId}" + $" Correlation id: {@event.CorrelationId}");
            await callConnection.HangUpAsync(forEveryone: true);
        }
        if (@event is PlayFailed { OperationContext: "ResponseToDtmf" })
        {
            logger.LogInformation($"PlayFailed event received for call connection id: {@event.CallConnectionId}" + $" Correlation id: {@event.CorrelationId}");
            await callConnection.HangUpAsync(forEveryone: true);
        }
        if (@event is AddParticipantSucceeded addParticipantSucceeded)
        {
            addedParticipantsCount++;
            logger.LogInformation($"participant added ---> {addParticipantSucceeded.Participant.RawId}");
            if ((addedParticipantsCount + declineParticipantsCount) == addedParticipants.Length)
            {
                await PerformHangUp(callConnection);
            }
        }
        if (@event is AddParticipantFailed failedParticipant)
        {
            declineParticipantsCount++;
            logger.LogInformation($"Failed participant Reason -------> {failedParticipant.ResultInformation?.Message}");
            if ((addedParticipantsCount + declineParticipantsCount) == addedParticipants.Length)
            {
                await PerformHangUp(callConnection);
            }
        }
        if (@event is RemoveParticipantSucceeded)
        {
            RemoveParticipantSucceeded RemoveParticipantSucceeded = (RemoveParticipantSucceeded)@event;
            logger.LogInformation($"Remove Participant Succeeded RawId : {RemoveParticipantSucceeded.Participant.RawId}");
        }
        if (@event is RemoveParticipantFailed)
        {
            RemoveParticipantFailed removeParticipantFailed = (RemoveParticipantFailed)@event;
            logger.LogInformation($"Remove participant failed RawId:{removeParticipantFailed.Participant.RawId}");
        }
        if (@event is ParticipantsUpdated updatedParticipantEvent)
        {
            logger.LogInformation($"Participant Updated Event Recieved");
            logger.LogInformation($"------- Updated Participant : {updatedParticipantEvent.Participants.Count} -------- ");
            foreach (var participant in updatedParticipantEvent.Participants)
            {
                logger.LogInformation($"Participant Raw ID : {participant.Identifier.RawId},  IsMuted : {participant.IsMuted}");
            }
        }
    }

    //Perform HangUp
    async Task PerformHangUp(CallConnection callConnection)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));

        var participantlistResponse = await callConnection.GetParticipantsAsync();
        logger.LogInformation($"-------Participant List  : {participantlistResponse.Value.Count} ----- ");
        foreach (var participant in participantlistResponse.Value)
        {
            try
            {
                logger.LogInformation($"{participant.Identifier.RawId}");
                var response = callConnection.GetParticipant(participant.Identifier);
                logger.LogInformation($"-------get participnat response  : {response} ----- ");
            }
            catch(Exception ex)
            {
                logger.LogInformation($"------Error In GetParticipant() for participnat : {participant.Identifier.RawId} " +
                    $"-----> {ex.Message}");
            }
            
        }

        int hangupScenario = callConfiguration.Value.HangUpScenarios;
        if (hangupScenario == 1)
        {
            logger.LogInformation($"CA hanging up the call for everyone." + $"Information of Call:{callConnection.GetCallConnectionProperties()}");
            var response = await callConnection.HangUpAsync(true);
            logger.LogInformation($"Hang up response : {response}");
        }
        else if (hangupScenario == 2)
        {
            logger.LogInformation($"CA hang up the call." + $"Information of Call:{callConnection.GetCallConnectionProperties()}");
            var response = await callConnection.HangUpAsync(false);
            logger.LogInformation($"Hang up response : {response}");
        }
        else if (hangupScenario == 3 || hangupScenario == 4)
        {
            if (addedParticipantsCount == 0 && hangupScenario == 3)
            {
                logger.LogInformation($"No participants got addedd to remove");
            }
            else
            {
                logger.LogInformation($"Going to remove added partipants.");
                List<CallParticipant> participantsToRemoveAll = (await callConnection.GetParticipantsAsync()).Value.ToList();
                CommunicationIdentifier targetParticipant = null;
                foreach (CallParticipant participantToRemove in participantsToRemoveAll)
                {
                    if (!string.IsNullOrEmpty(participantToRemove.Identifier.ToString())) 
                    {
                        if (participantToRemove.Identifier.RawId.Contains(TargetIdentity))
                        {
                            targetParticipant = participantToRemove.Identifier;
                        }
                        else if (participantToRemove.Identifier.RawId.Contains(SourceId))
                        {
                            SourceId = participantToRemove.Identifier.RawId;
                        }
                        else
                        {
                            var RemoveParticipant = new RemoveParticipantOptions(participantToRemove.Identifier);
                            logger.LogInformation($"going to remove participant : {participantToRemove.Identifier.RawId}");
                            var removeParticipantResponse = await callConnection.RemoveParticipantAsync(RemoveParticipant);
                            logger.LogInformation($"Removing participant Response : {removeParticipantResponse.GetRawResponse()}");
                        }
                    }
                }
                if (hangupScenario == 4 && targetParticipant != null)
                {
                    logger.LogInformation($"going to remove target participant : {targetParticipant.RawId}");
                    var removeParticipantResponse = await callConnection.RemoveParticipantAsync(targetParticipant);
                    logger.LogInformation($"Removing participant Response : {removeParticipantResponse.GetRawResponse()}");
                }
            }
        }
    }

    return Results.Ok();
}).Produces(StatusCodes.Status200OK);



// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
           Path.Combine(builder.Environment.ContentRootPath, "audio")),
    RequestPath = "/audio"
});

app.UseHttpsRedirection();
app.Run();
public enum CommunicationIdentifierKind
{
    PhoneIdentity,
    UserIdentity,
    UnknownIdentity

}
public class Constants
{
    public const string userIdentityRegex = @"8:acs:[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12}_[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12}";
    public const string phoneIdentityRegex = @"^\+\d{10,14}$";

}
