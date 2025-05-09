// SPDX-FileCopyrightText: 2024 Milon <milonpl.git@proton.me>
// SPDX-FileCopyrightText: 2024 Skubman <ba.fallaria@gmail.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <aiden@djkraz.com>
// SPDX-FileCopyrightText: 2025 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2025 Tobias Berger <toby@tobot.dev>
// SPDX-FileCopyrightText: 2025 deltanedas <39013340+deltanedas@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 deltanedas <@deltanedas:kde.org>
// SPDX-FileCopyrightText: 2025 gus <august.eymann@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Shared._DV.CartridgeLoader.Cartridges;
using Robust.Client.AutoGenerated;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.XAML;
using Robust.Client.UserInterface;
using Robust.Shared.Timing;

namespace Content.Client._DV.CartridgeLoader.Cartridges;

[GenerateTypedNameReferences]
public sealed partial class NanoChatUiFragment : BoxContainer
{
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly NewChatPopup _newChatPopup;
    private uint? _currentChat;
    private uint? _pendingChat;
    private uint _ownNumber;
    private bool _notificationsMuted;
    private bool _listNumber = true;
    private Dictionary<uint, NanoChatRecipient> _recipients = new();
    private Dictionary<uint, List<NanoChatMessage>> _messages = new();

    public event Action<NanoChatUiMessageType, uint?, string?, string?>? OnMessageSent;

    public NanoChatUiFragment()
    {
        IoCManager.InjectDependencies(this);
        RobustXamlLoader.Load(this);

        _newChatPopup = new NewChatPopup();
        SetupEventHandlers();
    }

    private void SetupEventHandlers()
    {
        _newChatPopup.OnChatCreated += (number, name, job) =>
        {
            OnMessageSent?.Invoke(NanoChatUiMessageType.NewChat, number, name, job);
        };

        NewChatButton.OnPressed += _ =>
        {
            _newChatPopup.ClearInputs();
            _newChatPopup.OpenCentered();
        };

        MuteButton.OnPressed += _ =>
        {
            _notificationsMuted = !_notificationsMuted;
            UpdateMuteButton();
            OnMessageSent?.Invoke(NanoChatUiMessageType.ToggleMute, null, null, null);
        };

        MessageInput.OnTextChanged += args =>
        {
            var length = args.Text.Length;
            var isValid = !string.IsNullOrWhiteSpace(args.Text) &&
                          length <= NanoChatMessage.MaxContentLength &&
                          (_currentChat != null || _pendingChat != null);

            SendButton.Disabled = !isValid;

            // Show character count when over limit
            CharacterCount.Visible = length > NanoChatMessage.MaxContentLength;
            if (length > NanoChatMessage.MaxContentLength)
            {
                CharacterCount.Text = Loc.GetString("nano-chat-message-too-long",
                    ("current", length),
                    ("max", NanoChatMessage.MaxContentLength));
                CharacterCount.StyleClasses.Add("LabelDanger");
            }
        };

        LookupButton.OnPressed += _ => ToggleView();
        LookupView.OnStartChat += contact =>
        {
            if (OnMessageSent is { } handler)
            {
                handler(NanoChatUiMessageType.NewChat, contact.Number, contact.Name, contact.JobTitle);
                SelectChat(contact.Number);
                ToggleView();
            }
        };
        ListNumberButton.OnPressed += _ =>
        {
            _listNumber = !_listNumber;
            UpdateListNumber();
            OnMessageSent?.Invoke(NanoChatUiMessageType.ToggleListNumber, null, null, null);
        };

        MessageInput.OnTextEntered += _ => SendMessage(); // Send message when pressing enter
        SendButton.OnPressed += _ => SendMessage();
        DeleteChatButton.OnPressed += _ => DeleteCurrentChat();
    }

    private void ToggleView()
    {
        ChatView.Visible = !ChatView.Visible;
        LookupView.Visible = !ChatView.Visible;
        LookupButton.Pressed = LookupView.Visible;
    }

    private void SendMessage()
    {
        var activeChat = _pendingChat ?? _currentChat;
        if (activeChat == null || string.IsNullOrWhiteSpace(MessageInput.Text))
            return;

        var messageContent = MessageInput.Text;
        if (!string.IsNullOrWhiteSpace(messageContent))
        {
            messageContent = messageContent.Trim();
            if (messageContent.Length > NanoChatMessage.MaxContentLength)
                messageContent = messageContent[..NanoChatMessage.MaxContentLength];
        }

        // Add predicted message
        var predictedMessage = new NanoChatMessage(
            _timing.CurTime,
            messageContent,
            _ownNumber
        );

        if (!_messages.TryGetValue(activeChat.Value, out var value))
        {
            value = new List<NanoChatMessage>();
            _messages[activeChat.Value] = value;
        }

        value.Add(predictedMessage);

        // Update UI with predicted message
        UpdateMessages(_messages);

        // Send message event
        OnMessageSent?.Invoke(NanoChatUiMessageType.SendMessage, activeChat, messageContent, null);

        // Clear input
        MessageInput.Text = string.Empty;
        SendButton.Disabled = true;
    }

    private void SelectChat(uint number)
    {
        // Don't reselect the same chat
        if (_currentChat == number && _pendingChat == null)
            return;

        _pendingChat = number;

        // Predict marking messages as read
        if (_recipients.TryGetValue(number, out var recipient))
        {
            recipient.HasUnread = false;
            _recipients[number] = recipient;
            UpdateChatList(_recipients);
        }

        OnMessageSent?.Invoke(NanoChatUiMessageType.SelectChat, number, null, null);
        UpdateCurrentChat();
    }

    private void DeleteCurrentChat()
    {
        var activeChat = _pendingChat ?? _currentChat;
        if (activeChat == null)
            return;

        OnMessageSent?.Invoke(NanoChatUiMessageType.DeleteChat, activeChat, null, null);
    }

    private void UpdateChatList(Dictionary<uint, NanoChatRecipient> recipients)
    {
        ChatList.RemoveAllChildren();
        _recipients = recipients;

        NoChatsLabel.Visible = recipients.Count == 0;
        if (NoChatsLabel.Parent != ChatList)
        {
            NoChatsLabel.Parent?.RemoveChild(NoChatsLabel);
            ChatList.AddChild(NoChatsLabel);
        }

        foreach (var (number, recipient) in recipients.OrderBy(r => r.Value.Name))
        {
            var entry = new NanoChatEntry();
            // For pending chat selection, always show it as selected even if unconfirmed
            var isSelected = (_pendingChat == number) || (_pendingChat == null && _currentChat == number);
            entry.SetRecipient(recipient, number, isSelected);
            entry.OnPressed += SelectChat;
            ChatList.AddChild(entry);
        }
    }

    private void UpdateCurrentChat()
    {
        var activeChat = _pendingChat ?? _currentChat;
        var hasActiveChat = activeChat != null;

        // Update UI state
        MessagesScroll.Visible = hasActiveChat;
        CurrentChatName.Visible = !hasActiveChat;
        MessageInputContainer.Visible = hasActiveChat;
        DeleteChatButton.Visible = hasActiveChat;
        DeleteChatButton.Disabled = !hasActiveChat;

        if (activeChat != null && _recipients.TryGetValue(activeChat.Value, out var recipient))
        {
            CurrentChatName.Text = recipient.Name + (string.IsNullOrEmpty(recipient.JobTitle) ? "" : $" ({recipient.JobTitle})");
        }
        else
        {
            CurrentChatName.Text = Loc.GetString("nano-chat-select-chat");
        }
    }

    private void UpdateMessages(Dictionary<uint, List<NanoChatMessage>> messages)
    {
        _messages = messages;
        MessageList.RemoveAllChildren();

        var activeChat = _pendingChat ?? _currentChat;
        if (activeChat == null || !messages.TryGetValue(activeChat.Value, out var chatMessages))
            return;

        foreach (var message in chatMessages)
        {
            var messageBubble = new NanoChatMessageBubble();
            messageBubble.SetMessage(message, message.SenderId == _ownNumber);
            MessageList.AddChild(messageBubble);

            // Add spacing between messages
            MessageList.AddChild(new Control { MinSize = new Vector2(0, 4) });
        }

        MessageList.InvalidateMeasure();
        MessagesScroll.InvalidateMeasure();

        // Scroll to bottom after messages are added
        if (MessageList.Parent is ScrollContainer scroll)
            scroll.SetScrollValue(new Vector2(0, float.MaxValue));
    }

    private void UpdateMuteButton()
    {
        if (BellMutedIcon != null)
            BellMutedIcon.Visible = _notificationsMuted;
    }

    private void UpdateListNumber()
    {
        if (ListNumberButton != null)
            ListNumberButton.Pressed = _listNumber;
    }

    public void UpdateState(NanoChatUiState state)
    {
        _ownNumber = state.OwnNumber;
        _notificationsMuted = state.NotificationsMuted;
        _listNumber = state.ListNumber;
        OwnNumberLabel.Text = $"#{state.OwnNumber:D4}";
        UpdateMuteButton();
        UpdateListNumber();

        // Update new chat button state based on recipient limit
        var atLimit = state.Recipients.Count >= state.MaxRecipients;
        NewChatButton.Disabled = atLimit;
        NewChatButton.ToolTip = atLimit
            ? Loc.GetString("nano-chat-max-recipients")
            : Loc.GetString("nano-chat-new-chat");

        // First handle pending chat resolution if we have one
        if (_pendingChat != null)
        {
            if (_pendingChat == state.CurrentChat)
                _currentChat = _pendingChat; // Server confirmed our selection

            _pendingChat = null; // Clear pending either way
        }

        // No pending chat or it was just cleared, update current directly
        if (_pendingChat == null)
            _currentChat = state.CurrentChat;

        UpdateCurrentChat();
        UpdateChatList(state.Recipients);
        UpdateMessages(state.Messages);
        LookupView.UpdateContactList(state);
    }
}