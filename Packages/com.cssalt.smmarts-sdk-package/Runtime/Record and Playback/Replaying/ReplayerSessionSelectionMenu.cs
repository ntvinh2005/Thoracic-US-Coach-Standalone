using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine.EventSystems;
using System.IO;

namespace SMMARTS
{
    /// <summary>
    /// Manages a scrollable list of available recording sessions for replay selection.
    /// Populates list on enable, sorts sessions by date (newest first), handles selection.
    /// </summary>
    public class ReplayerSessionSelectionMenu : MonoBehaviour
    {
        [Header("UI References")]
        [Tooltip("ScrollRect component for the session list")]
        public ScrollRect scrollRect;
        public GameObject State_1_None;
        public GameObject State_3_Replaying;
        [SerializeField] TMP_Text SessionDirectory;

        [Tooltip("Content transform where session items will be instantiated")]
        public Transform contentParent;

        [Tooltip("Prefab for individual session list items (should have Button + TextMeshProUGUI)")]
        public GameObject sessionItemPrefab;

        [Header("Debug Info")]
        [SerializeField] private int sessionCount = 0;
        [SerializeField] private List<string> sortedSessions = new List<string>();

        [Header("Comment System UI")]
        [Tooltip("Text field for displaying and editing session comments")]
        public TMP_InputField commentTextField;

        [Tooltip("Play Session button")]
        public Button playSessionButton;

        [Tooltip("Save Comment button")]
        public Button saveCommentButton;

        [Tooltip("Delete Session button")]
        public Button deleteSessionButton;

        [Header("Visual Settings")]
        [Tooltip("Color for selected session button")]
        public Color selectedButtonColor = Color.yellow;

        [Tooltip("Default color for session buttons")]
        public Color defaultButtonColor = Color.white;

        [Header("Comment Editing State")]
        [SerializeField] private bool isEditingComments = false;
        [SerializeField] private string editButtonText = "Edit Comments";
        [SerializeField] private string saveButtonText = "Save Comments";

        [Header("Debug Info - Comment System")]
        [SerializeField] private string selectedSessionID = "";
        [SerializeField] private Button selectedButton = null;
        [SerializeField] private bool isSessionSelected = false;



        void Start()
        {
            // Auto-find components if not assigned
            if (scrollRect == null)
                scrollRect = GetComponentInChildren<ScrollRect>();
            if (contentParent == null && scrollRect != null)
                contentParent = scrollRect.content;

            if (scrollRect == null || contentParent == null)
            {
                Debug.LogError("SessionSelectionMenu: Missing ScrollRect or Content references!");
            }

            // Initialize comment system
            InitializeCommentSystem();
        }

        /// <summary>
        /// Initialize the comment system UI
        /// </summary>
        private void InitializeCommentSystem()
        {
            // Set initial state - no session selected
            SetSessionSelected(false);

            // Set up button listeners
            if (playSessionButton != null)
                playSessionButton.onClick.AddListener(OnPlaySessionClicked);

            if (saveCommentButton != null)
                saveCommentButton.onClick.AddListener(OnSaveCommentClicked);

            if (deleteSessionButton != null)
                deleteSessionButton.onClick.AddListener(OnDeleteSessionClicked);

            // Clear comment field
            if (commentTextField != null)
            {
                commentTextField.text = "";
                commentTextField.placeholder.GetComponent<TextMeshProUGUI>().text = "No session selected";
            }
        }

        /// <summary>
        /// Create a UI item for a single session with hover and click functionality
        /// </summary>
        private void CreateSessionItem(string sessionID)
        {
            GameObject item = Instantiate(sessionItemPrefab, contentParent);

            // Set the text
            TextMeshProUGUI text = item.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                text.text = sessionID;
            }

            // Get the button component
            Button button = item.GetComponent<Button>();
            if (button == null)
            {
                Debug.LogWarning($"SessionSelectionMenu: No Button component found on session item for {sessionID}");
                return;
            }

            // Set up button click - now selects instead of immediately playing
            button.onClick.AddListener(() => OnSessionClicked(sessionID, button));

            // Set up hover functionality using EventTrigger
            EventTrigger eventTrigger = item.GetComponent<EventTrigger>();
            if (eventTrigger == null)
            {
                Debug.LogWarning($"SessionSelectionMenu: No EventTrigger component found on session item for {sessionID}");
                return;
            }

            // Add hover enter event
            EventTrigger.Entry hoverEnter = new EventTrigger.Entry();
            hoverEnter.eventID = EventTriggerType.PointerEnter;
            hoverEnter.callback.AddListener((data) => OnSessionHover(sessionID));
            eventTrigger.triggers.Add(hoverEnter);

            // Add hover exit event
            EventTrigger.Entry hoverExit = new EventTrigger.Entry();
            hoverExit.eventID = EventTriggerType.PointerExit;
            hoverExit.callback.AddListener((data) => OnSessionHoverExit(sessionID));
            eventTrigger.triggers.Add(hoverExit);

            // Set default button color
            ColorBlock colors = button.colors;
            colors.normalColor = defaultButtonColor;
            button.colors = colors;
        }

        /// <summary>
        /// Handle session hover - show comments in text field
        /// </summary>
        private void OnSessionHover(string sessionID)
        {
            if (isSessionSelected)
                return; // Don't change comments if a session is selected/locked

            ShowCommentsForSession(sessionID);
        }

        /// <summary>
        /// Handle session hover exit
        /// </summary>
        private void OnSessionHoverExit(string sessionID)
        {
            if (isSessionSelected)
                return; // Don't clear comments if a session is selected/locked

            // Clear comments field or show placeholder
            if (commentTextField != null)
            {
                commentTextField.text = "";
                commentTextField.placeholder.GetComponent<TextMeshProUGUI>().text = "Hover over a session to preview comments";
            }
        }

        /// <summary>
        /// Handle session button click - select/lock the session, or deselect if already selected
        /// </summary>
        private void OnSessionClicked(string sessionID, Button button)
        {
            Debug.Log($"Session clicked: {sessionID}");

            // If clicking the same session that's already selected, deselect it
            if (isSessionSelected && selectedSessionID == sessionID && selectedButton == button)
            {
                ClearSessionSelection();
                ShowCommentsForSession(sessionID); // this reloads the comment text box in grey, identical to what it would do if you mouseover'd it because the session button is still under your mouse. 
                return;
            }

            // Deselect previous button if any
            if (selectedButton != null)
            {
                ColorBlock colors = selectedButton.colors;
                colors.normalColor = defaultButtonColor;
                selectedButton.colors = colors;
            }

            // Select new button
            selectedSessionID = sessionID;
            selectedButton = button;

            // Set selected button color
            ColorBlock selectedColors = button.colors;
            selectedColors.normalColor = selectedButtonColor;
            button.colors = selectedColors;

            // Show comments and enable interaction
            ShowCommentsForSession(sessionID);
            SetSessionSelected(true);

            // Create comment file if it doesn't exist
            if (CommentManager.ME != null && !CommentManager.ME.CommentFileExists(sessionID))
            {
                CommentManager.ME.CreateEmptyCommentFile(sessionID);
                Debug.Log($"Created empty comment file for session: {sessionID}");
            }
        }

        /// <summary>
        /// Show comments for a specific session in the text field
        /// </summary>
        private void ShowCommentsForSession(string sessionID)
        {
            if (commentTextField == null || CommentManager.ME == null)
                return;

            string comments = CommentManager.ME.ReadCommentFile(sessionID);

            if (string.IsNullOrEmpty(comments))
            {
                commentTextField.text = "";
                commentTextField.placeholder.GetComponent<TextMeshProUGUI>().text = "No comments";
            }
            else
            {
                commentTextField.text = comments;
                commentTextField.placeholder.GetComponent<TextMeshProUGUI>().text = "";
            }
        }

        /// <summary>
        /// Set the session selection state and update UI accordingly
        /// </summary>
        private void SetSessionSelected(bool selected)
        {
            isSessionSelected = selected;

            // Always start in view mode when selecting a session
            isEditingComments = false;

            // Enable/disable action buttons
            if (playSessionButton != null)
                playSessionButton.interactable = selected;

            if (saveCommentButton != null)
            {
                saveCommentButton.interactable = selected;
                // Set to Edit mode by default
                if (selected)
                {
                    saveCommentButton.GetComponentInChildren<TextMeshProUGUI>().text = editButtonText;
                }
            }

            if (deleteSessionButton != null)
                deleteSessionButton.interactable = selected;

            // Comment field starts as non-editable when session is selected
            if (commentTextField != null)
                commentTextField.interactable = false;
        }

        /// <summary>
        /// Handle Edit/Save Comment button click - toggles between edit and save modes
        /// </summary>
        private void OnSaveCommentClicked()
        {
            if (string.IsNullOrEmpty(selectedSessionID) || saveCommentButton == null)
                return;

            if (!isEditingComments)
            {
                // Switch to edit mode
                isEditingComments = true;
                commentTextField.interactable = true;
                saveCommentButton.GetComponentInChildren<TextMeshProUGUI>().text = saveButtonText;
                Debug.Log("Entered comment edit mode");
            }
            else
            {
                // Save and switch back to view mode
                if (commentTextField != null && CommentManager.ME != null)
                {
                    string commentContent = commentTextField.text;
                    bool success = CommentManager.ME.WriteCommentFile(selectedSessionID, commentContent);

                    if (success)
                    {
                        Debug.Log($"Comments saved for session: {selectedSessionID}");

                        // Switch back to view mode
                        isEditingComments = false;
                        commentTextField.interactable = false;
                        saveCommentButton.GetComponentInChildren<TextMeshProUGUI>().text = editButtonText;
                    }
                    else
                    {
                        Debug.LogError($"Failed to save comments - staying in edit mode");
                    }
                }
            }
        }
        /// <summary>
        /// Handle Play Session button click
        /// </summary>
        private void OnPlaySessionClicked()
        {
            if (string.IsNullOrEmpty(selectedSessionID))
                return;

            Debug.Log($"Playing session: {selectedSessionID}");

            // Start replay of selected session (same as old behavior)
            if (ReplayerManager.ME != null)
            {
                ReplayerManager.ME.ReplaySession(selectedSessionID);
            }

            // Close this menu
            CloseMenu();
        }




        void OnEnable()
        {
            // display the recorded session directory for this simulator on the UI.
            if (SessionDirectory != null && RecordingManager.ME != null) SessionDirectory.text = RecordingManager.ME.GetBaseRecordingDirectory();

            // Populate the session list whenever this menu is activated
            PopulateSessionList();
        }

        /// <summary>
        /// Populate the scroll list with available sessions
        /// </summary>
        private void PopulateSessionList()
        {
            if (ReplayerManager.ME == null)
            {
                Debug.LogWarning("SessionSelectionMenu: ReplayerManager not found!");
                return;
            }

            if (contentParent == null || sessionItemPrefab == null)
            {
                Debug.LogWarning("SessionSelectionMenu: Missing content parent or item prefab!");
                return;
            }

            // Clear existing items
            ClearSessionList();

            // Get and sort sessions
            List<string> availableSessions = ReplayerManager.ME.GetAvailableSessions();
            sortedSessions = SortSessionsByDate(availableSessions);
            sessionCount = sortedSessions.Count;

            Debug.Log($"Populating session menu with {sessionCount} sessions");

            // Create UI items for each session
            foreach (string sessionID in sortedSessions)
            {
                CreateSessionItem(sessionID);
            }
        }

        /// <summary>
        /// Sort sessions by date (newest first) based on timestamp in SessionID
        /// </summary>
        private List<string> SortSessionsByDate(List<string> sessions)
        {
            return sessions.OrderByDescending(sessionID => sessionID).ToList();
        }



        /// <summary>
        /// Clear all session items from the list
        /// </summary>
        private void ClearSessionList()
        {
            if (contentParent == null) return;

            foreach (Transform child in contentParent)
            {
                Destroy(child.gameObject);
            }
        }

        /// <summary>
        /// Close the session selection menu
        /// </summary>
        public void CloseMenu()
        {
            State_1_None.SetActive(false);
            State_3_Replaying.SetActive(true);
            transform.parent.gameObject.SetActive(false);
        }

        /// <summary>
        /// Public method to refresh the session list
        /// </summary>
        public void RefreshSessions()
        {
            if (ReplayerManager.ME != null)
            {
                ReplayerManager.ME.RefreshAvailableSessions();
            }
            PopulateSessionList();
        }

        /// <summary>
        /// Context menu helper for testing
        /// </summary>
        [ContextMenu("Populate Session List")]
        public void PopulateSessionListButton() => PopulateSessionList();

        /// <summary>
        /// Handle Delete Session button click - soft delete by moving to "Deleted Sessions" folder
        /// </summary>
        private void OnDeleteSessionClicked()
        {
            if (string.IsNullOrEmpty(selectedSessionID))
                return;

            if (ReplayerManager.ME == null)
            {
                Debug.LogError("ReplayerManager not found - cannot delete session");
                return;
            }

            // Get the base recording directory
            string baseRecordingDirectory = ReplayerManager.ME.baseRecordingDirectory;

            // Source: current session directory
            string sourceDirectory = ReplayerManager.ME.GetSessionDirectory(selectedSessionID);

            // Destination: "Deleted Sessions" folder
            string deletedSessionsDirectory = Path.Combine(baseRecordingDirectory, "Deleted Sessions");
            string destinationDirectory = Path.Combine(deletedSessionsDirectory, selectedSessionID);

            try
            {
                // Create "Deleted Sessions" directory if it doesn't exist
                if (!Directory.Exists(deletedSessionsDirectory))
                {
                    Directory.CreateDirectory(deletedSessionsDirectory);
                    Debug.Log($"Created Deleted Sessions directory: {deletedSessionsDirectory}");
                }

                // Check if source directory exists
                if (!Directory.Exists(sourceDirectory))
                {
                    Debug.LogError($"Source session directory not found: {sourceDirectory}");
                    return;
                }

                // Check if destination already exists (handle naming conflict)
                if (Directory.Exists(destinationDirectory))
                {
                    // Add timestamp to make it unique
                    string timestamp = System.DateTime.Now.ToString("_yyyy-MM-dd_HH-mm-ss");
                    destinationDirectory = Path.Combine(deletedSessionsDirectory, selectedSessionID + timestamp);
                }

                // Move the directory
                Directory.Move(sourceDirectory, destinationDirectory);

                Debug.Log($"Session moved to deleted folder: {selectedSessionID}");
                Debug.Log($"From: {sourceDirectory}");
                Debug.Log($"To: {destinationDirectory}");

                // Clear selection state
                ClearSessionSelection();

                // Refresh the session list to remove the deleted item
                RefreshSessions();

            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to delete session {selectedSessionID}: {e.Message}");
            }
        }

        /// <summary>
        /// Clear the current session selection and reset UI
        /// </summary>
        private void ClearSessionSelection()
        {
            // Reset selected button color if it exists
            if (selectedButton != null)
            {
                ColorBlock colors = selectedButton.colors;
                colors.normalColor = defaultButtonColor;
                selectedButton.colors = colors;
            }

            // Clear selection state
            selectedSessionID = "";
            selectedButton = null;
            isEditingComments = false;

            // Disable action buttons and comment field
            SetSessionSelected(false);

            // Clear comment field and set hover placeholder
            if (commentTextField != null)
            {
                commentTextField.text = "";
                commentTextField.placeholder.GetComponent<TextMeshProUGUI>().text = "Hover over a session to preview comments";
            }

            Debug.Log("Session selection cleared");
        }



    }




}