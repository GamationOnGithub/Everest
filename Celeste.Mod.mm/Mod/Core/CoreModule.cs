﻿using Celeste.Editor;
using Celeste.Mod.UI;
using FMOD.Studio;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using NLua;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Celeste.Mod.Core {
    /// <summary>
    /// The Everest core module class. Feel free to access the core module settings from your own mod.
    /// </summary>
    public class CoreModule : EverestModule {

        public static CoreModule Instance;

        public override Type SettingsType => typeof(CoreModuleSettings);
        // Default values for anything using Settings too early (f.e. --dump-all)
        public static CoreModuleSettings Settings => (CoreModuleSettings) Instance?._Settings ?? new CoreModuleSettings();

        public override Type SaveDataType => typeof(CoreModuleSaveData);
        public static CoreModuleSaveData SaveData => (CoreModuleSaveData) Instance._SaveData;

        public override Type SessionType => typeof(CoreModuleSession);
        public static CoreModuleSession Session => (CoreModuleSession) Instance._Session;

        private static ILHook nluaAssemblyGetTypesHook;

        public CoreModule() {
            Instance = this;

            // Runtime modules shouldn't do this.
            Metadata = new EverestModuleMetadata() {
                Name = "Everest",
                VersionString = Everest.VersionString
            };
        }

        public override void LoadSettings() {
            base.LoadSettings();

            // If we're running in an environment that prefers those flag, forcibly enable them.
            Settings.LazyLoading |= Everest.Flags.PreferLazyLoading;

            // If using FNA with DISABLE_THREADING, forcibly enable non-threaded GL.
            // Note: This isn't accurate, as it doesn't check which GL device is being used.
            Type t_OpenGLDevice = typeof(Game).Assembly.GetType("Microsoft.Xna.Framework.Graphics.OpenGLDevice");
            if (typeof(Game).Assembly.FullName.Contains("FNA") &&
                t_OpenGLDevice != null &&
                t_OpenGLDevice.GetMethod("ForceToMainThread", BindingFlags.NonPublic | BindingFlags.Instance) == null) {
                Settings.NonThreadedGL = true;
            }
        }

        public override void SaveSettings() {
            Settings.CurrentVersion = Everest.Version.ToString();

            base.SaveSettings();
        }

        public override void Load() {
            Everest.Events.MainMenu.OnCreateButtons += CreateMainMenuButtons;
            Everest.Events.Level.OnCreatePauseMenuButtons += CreatePauseMenuButtons;
            nluaAssemblyGetTypesHook = new ILHook(typeof(Lua).Assembly.GetType("NLua.Extensions.TypeExtensions").GetMethod("GetExtensionMethods"), patchNLuaAssemblyGetTypes);

            if (Everest.Flags.IsMobile) {
                // It shouldn't look that bad on mobile screens...
                Environment.SetEnvironmentVariable("FNA_OPENGL_BACKBUFFER_SCALE_NEAREST", "1");
            }

            foreach (KeyValuePair<string, LogLevel> logLevel in Settings.LogLevels) {
                Logger.SetLogLevelFromYaml(logLevel.Key, logLevel.Value);
            }

            if (Directory.Exists("LogHistory")) {
                int historyToKeep = Math.Max(Settings.LogHistoryCountToKeep, 0); // just in case someone tries to set the value to -42
                while (Directory.GetFiles("LogHistory", "log_*.txt").Length > historyToKeep) {
                    // we have to delete 1 more file: the first in alphabetical order, which should be the oldest.
                    List<string> files = Directory.GetFiles("LogHistory", "log_*.txt").ToList();
                    files.Sort();

                    Logger.Log("core", $"log.txt history: keeping {historyToKeep} file(s) of history, deleting {files[0]}");
                    File.Delete(files[0]);
                }
            }
        }

        public override void Initialize() {
            // F5: Reload and restart the current screen.
            Engine.Commands.FunctionKeyActions[4] = () => {
                // CTRL + F5: Quick-restart the entire game.
                if (MInput.Keyboard.Check(Keys.LeftControl) ||
                    MInput.Keyboard.Check(Keys.RightControl)) {

                    // block restarting while the game is starting up. this might lead to crashes
                    if (!(Engine.Scene is GameLoader)) {
                        Everest.QuickFullRestart();
                    }

                    return;
                }

                if (!(Engine.Scene is Level level))
                    return;

                AssetReloadHelper.Do(Dialog.Clean("ASSETRELOADHELPER_RELOADINGMAP"), () => {
                    AreaData.Areas[level.Session.Area.ID].Mode[(int) level.Session.Area.Mode].MapData.Reload();
                });
                AssetReloadHelper.ReloadLevel();
            };

            // F6: Open map editor for current level.
            Engine.Commands.FunctionKeyActions[5] = () => {
                if (!(Engine.Scene is Level level))
                    return;
                Engine.Scene = new MapEditor(level.Session.Area);
                Engine.Commands.Open = false;
            };

            // Set up the touch input regions.
            TouchRegion touchTitleScreen = new TouchRegion {
                Position = new Vector2(1920f, 1080f) * 0.5f,
                Size = new Vector2(1920f, 1080f),
                Condition = _ =>
                    ((Engine.Scene as Overworld)?.IsCurrent<OuiTitleScreen>() ?? false) ||
                    (Engine.Scene is GameLoader)
                ,
                Button = Input.MenuConfirm
            };
        }

        public override void OnInputInitialize() {
            base.OnInputInitialize();

            // Set up repeating on the "menu page up" and "menu page down" buttons like "menu up" and "menu down".
            Settings.MenuPageUp.Button.SetRepeat(0.4f, 0.1f);
            Settings.MenuPageDown.Button.SetRepeat(0.4f, 0.1f);
        }

        public override void LoadContent(bool firstLoad) {
            // Check if the current input GUI override is still valid. If so, apply it.
            if (!string.IsNullOrEmpty(Settings.InputGui)) {
                string inputGuiPath = $"controls/{Settings.InputGui}/";
                if (GFX.Gui.GetTextures().Any(kvp => kvp.Key.StartsWith(inputGuiPath))) {
                    Input.OverrideInputPrefix = Settings.InputGui;
                } else {
                    Settings.InputGui = "";
                }
            }

            if (firstLoad && !Everest.Flags.AvoidRenderTargets) {
                SubHudRenderer.Buffer = VirtualContent.CreateRenderTarget("subhud-target", 1922, 1082);
            }
            if (Everest.Flags.AvoidRenderTargets && Celeste.HudTarget != null) {
                Celeste.HudTarget.Dispose();
                Celeste.HudTarget = null;
            }

            if (Settings.NonThreadedGL) {
                GFX.Load();
                MTN.Load();
                GFX.LoadData();
                MTN.LoadData();
            }
            // Otherwise loaded in GameLoader.LoadThread

            // Celeste 1.3.0.0 gets rid of those.
            for (int i = 0; i <= 29; i++)
                GFX.Game[$"objects/checkpoint/flag{i:D2}"] = GFX.Game["util/pixel"];
            for (int i = 0; i <= 27; i++)
                GFX.Game[$"objects/checkpoint/obelisk{i:D2}"] = GFX.Game["util/pixel"];

            GFX.Gui["fileselect/assist"] = GFX.Game["util/pixel"];
            GFX.Gui["fileselect/cheatmode"] = GFX.Game["util/pixel"];
        }

        private void patchNLuaAssemblyGetTypes(ILContext il) {
            ILCursor cursor = new ILCursor(il);

            while (cursor.TryGotoNext(instr => instr.MatchCallvirt<Assembly>("GetTypes"))) {
                Logger.Log("core", $"Redirecting Assembly.GetTypes => Extensions.GetTypesSafe in {il.Method.FullName}, index {cursor.Index}");
                cursor.Next.OpCode = OpCodes.Call;
                cursor.Next.Operand = typeof(Extensions).GetMethod("GetTypesSafe");
            }
        }

        public override void Unload() {
            Everest.Events.MainMenu.OnCreateButtons -= CreateMainMenuButtons;
            Everest.Events.Level.OnCreatePauseMenuButtons -= CreatePauseMenuButtons;
            nluaAssemblyGetTypesHook?.Dispose();
            nluaAssemblyGetTypesHook = null;
        }

        public void CreateMainMenuButtons(OuiMainMenu menu, List<MenuButton> buttons) {
            if (Everest.Flags.IsDisabled)
                return;

            int index;

            // Find the options button and place our button below it.
            index = buttons.FindIndex(_ => {
                MainMenuSmallButton other = (_ as MainMenuSmallButton);
                if (other == null)
                    return false;
                return other.GetLabelName() == "menu_options" && other.GetIconName() == "menu/options";
            });
            if (index != -1)
                index++;
            // Otherwise, place it above the exit button.
            else
                index = buttons.Count - 1;

            buttons.Insert(index, new MainMenuModOptionsButton("menu_modoptions", "menu/modoptions_new", menu, Vector2.Zero, Vector2.Zero, () => {
                Audio.Play(SFX.ui_main_button_select);
                Audio.Play(SFX.ui_main_whoosh_large_in);
                menu.Overworld.Goto<OuiModOptions>();
            }));
        }

        public void CreatePauseMenuButtons(Level level, TextMenu menu, bool minimal) {
            if (Everest.Flags.IsDisabled || !Settings.ShowModOptionsInGame)
                return;

            List<TextMenu.Item> items = menu.GetItems();
            int index;

            // Find the options button and place our button below it.
            string cleanedOptions = Dialog.Clean("menu_pause_options");
            index = items.FindIndex(_ => {
                TextMenu.Button other = (_ as TextMenu.Button);
                if (other == null)
                    return false;
                return other.Label == cleanedOptions;
            });
            if (index != -1)
                index++;
            // Otherwise, place it below the last button.
            else
                index = items.Count;

            TextMenu.Item itemModOptions = null;
            menu.Insert(index, itemModOptions = new TextMenu.Button(Dialog.Clean("menu_pause_modoptions")).Pressed(() => {
                int returnIndex = menu.IndexOf(itemModOptions);
                menu.RemoveSelf();

                level.PauseMainMenuOpen = false;
                level.Paused = true;

                TextMenu options = OuiModOptions.CreateMenu(true, LevelExt.PauseSnapshot);

                options.OnESC = options.OnCancel = () => {
                    Audio.Play(SFX.ui_main_button_back);
                    options.CloseAndRun(Everest.SaveSettings(), () => {
                        level.Pause(returnIndex, minimal, false);

                        // adjust the Mod Options menu position, in case it moved (pause menu entries added/removed after changing mod options).
                        TextMenu textMenu = level.Entities.GetToAdd().FirstOrDefault((Entity e) => e is TextMenu) as TextMenu;
                        TextMenu.Button modOptionsButton = textMenu?.GetItems().OfType<TextMenu.Button>()
                            .FirstOrDefault(button => button.Label == Dialog.Clean("menu_pause_modoptions"));
                        if (modOptionsButton != null) {
                            textMenu.Selection = textMenu.IndexOf(modOptionsButton);
                        }
                    });
                };

                options.OnPause = () => {
                    Audio.Play(SFX.ui_main_button_back);
                    options.CloseAndRun(Everest.SaveSettings(), () => {
                        level.Paused = false;
                        Engine.FreezeTimer = 0.15f;
                    });
                };

                level.Add(options);
            }));
        }

        public override void CreateModMenuSection(TextMenu menu, bool inGame, EventInstance snapshot) {
            // Optional - reload mod settings when entering the mod options.
            // LoadSettings();

            base.CreateModMenuSection(menu, inGame, snapshot);

            if (!inGame) {
                List<TextMenu.Item> items = menu.GetItems();

                // change the "key config" labels
                (items[items.Count - 2] as TextMenu.Button).Label = Dialog.Clean("MODOPTIONS_COREMODULE_KEYCONFIG") + " " + (items[items.Count - 2] as TextMenu.Button).Label;
                (items[items.Count - 1] as TextMenu.Button).Label = Dialog.Clean("MODOPTIONS_COREMODULE_KEYCONFIG") + " " + (items[items.Count - 1] as TextMenu.Button).Label;

                // insert extra options before the "key config" options
                menu.Insert(items.Count - 2, new TextMenu.Button(Dialog.Clean("modoptions_coremodule_oobe")).Pressed(() => {
                    OuiModOptions.Instance.Overworld.Goto<OuiOOBE>();
                }));

                menu.Insert(items.Count - 2, new TextMenu.Button(Dialog.Clean("modoptions_coremodule_soundtest")).Pressed(() => {
                    OuiModOptions.Instance.Overworld.Goto<OuiSoundTest>();
                }));

                menu.Insert(items.Count - 2, new TextMenu.Button(Dialog.Clean("modoptions_coremodule_versionlist")).Pressed(() => {
                    OuiModOptions.Instance.Overworld.Goto<OuiVersionList>();
                }));

                menu.Insert(items.Count - 2, new TextMenu.Button(Dialog.Clean("modoptions_coremodule_modupdates")).Pressed(() => {
                    OuiModOptions.Instance.Overworld.Goto<OuiModUpdateList>();
                }));

                menu.Insert(items.Count - 2, new TextMenu.Button(Dialog.Clean("modoptions_coremodule_togglemods")).Pressed(() => {
                    OuiModOptions.Instance.Overworld.Goto<OuiModToggler>();
                }));
            }
        }

        public override void PrepareMapDataProcessors(MapDataFixup context) {
            context.Add<CoreMapDataProcessor>();
        }

    }
}
