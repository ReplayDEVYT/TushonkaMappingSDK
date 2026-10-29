using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // JSON tab of the Server Data Editor: the whole base.json as text, for values the other tabs
    // don't cover. Edits are parsed on Apply (and on Save or leaving the tab) and replace the
    // loaded base.json, so the other tabs show them.
    public partial class TarkovServerDataWindow
    {
        private string _jsonText;
        private bool _jsonEdited;
        private string _jsonError;
        private static GUIStyle _jsonStyle;

        private void DrawJson()
        {
            if (_jsonText == null)
                RefreshJson();
            EditorGUILayout.LabelField("base.json", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("The whole file as it will be saved, with the other tabs' changes. Apply checks the JSON and passes " +
                                       "your edits to the other tabs; Save applies them too.", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_jsonEdited))
                {
                    if (GUILayout.Button(_jsonEdited ? "Apply*" : "Apply"))
                        ApplyJson();
                    if (GUILayout.Button("Revert"))
                        RefreshJson();
                }
                if (GUILayout.Button(new GUIContent("Open Saved File", "Opens base.json as last saved in your default editor.")))
                    EditorUtility.OpenWithDefaultApp(BasePath);
            }
            if (!string.IsNullOrEmpty(_jsonError))
                EditorGUILayout.HelpBox(_jsonError, MessageType.Error);

            if (_jsonStyle == null)
            {
                _jsonStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = false };
                var mono = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New" }, 12);
                if (mono != null)
                    _jsonStyle.font = mono;
            }
            var text = EditorGUILayout.TextArea(_jsonText, _jsonStyle, GUILayout.ExpandHeight(true));
            if (text != _jsonText)
            {
                _jsonText = text;
                _jsonEdited = true;
                _jsonError = null;
            }
        }

        private void RefreshJson()
        {
            PushFieldsToBase();
            _jsonText = _base.ToString(Formatting.Indented);
            _jsonEdited = false;
            _jsonError = null;
            GUI.FocusControl(null);
        }

        // replaces the loaded base.json with the edited text; false (and the error shown) if it doesn't parse
        private bool ApplyJson()
        {
            if (!_jsonEdited)
                return true;
            try
            {
                _base = JObject.Parse(_jsonText);
            }
            catch (JsonReaderException e)
            {
                _jsonError = e.Message;
                return false;
            }
            PullFieldsFromBase();
            _dirty = true;
            RefreshJson();
            return true;
        }

        // leaving the tab applies the edits; with broken JSON, asks whether to drop them
        private bool LeaveJsonTab()
        {
            if (ApplyJson())
                return true;
            if (!EditorUtility.DisplayDialog("Server Data", $"The JSON has an error:\n\n{_jsonError}\n\nDiscard your JSON edits?", "Discard", "Keep Editing"))
                return false;
            _jsonText = null;
            _jsonEdited = false;
            _jsonError = null;
            return true;
        }
    }
}
