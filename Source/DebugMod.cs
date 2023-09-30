using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
//we now use ienumerator so this is required (lost my mind over this)
using System.Collections;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Shims.NET.System.Reflection;
using Modding;
using MonoMod.ModInterop;
using UnityEngine;
using UnityEngine.SceneManagement;
using GlobalEnums;
using JetBrains.Annotations;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;
using DebugMod.TikTikCam;

namespace DebugMod
{
    public class DebugMod : Mod, IGlobalSettings<GlobalSettings>, ILocalSettings<SaveSettings>, ICustomMenuMod
    {
        public override string GetVersion()
        {
            Assembly asm = typeof(DebugMod).Assembly;
            string ver = asm.GetName().Version.ToString();

            using var sha1 = SHA1.Create();
            using FileStream stream = File.OpenRead(asm.Location);

            byte[] hashBytes = sha1.ComputeHash(stream);

            string hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            return $"{ver}-{hash.Substring(0, 6)}";
        }

        private static GameManager _gm;
        private static InputHandler _ih;
        private static HeroController _hc;
        private static GameObject _refKnight;
        private static PlayMakerFSM _refKnightSlash;
        private static CameraController _refCamera;
        private static PlayMakerFSM _refDreamNail;
        private static Collider2D _refHeroCollider;
        private static Collider2D _refHeroBox;

        internal static GameManager GM => _gm != null ? _gm : (_gm = GameManager.instance);
        internal static InputHandler IH => _ih != null ? _ih : (_ih = GM.inputHandler);
        internal static HeroController HC => _hc != null ? _hc : (_hc = HeroController.instance);
        internal static GameObject RefKnight => _refKnight != null ? _refKnight : (_refKnight = HC.gameObject);
        internal static PlayMakerFSM RefKnightSlash => _refKnightSlash != null ? _refKnightSlash : (_refKnightSlash = RefKnight.transform.Find("Attacks/Slash").GetComponent<PlayMakerFSM>());
        internal static CameraController RefCamera => _refCamera != null ? _refCamera : (_refCamera = GM.cameraCtrl);
        internal static PlayMakerFSM RefDreamNail => _refDreamNail != null ? _refDreamNail : (_refDreamNail = FSMUtility.LocateFSM(RefKnight, "Dream Nail"));
        internal static Collider2D RefHeroCollider => _refHeroCollider != null ? _refHeroCollider : (_refHeroCollider = RefKnight.GetComponent<Collider2D>());
        internal static Collider2D RefHeroBox => _refHeroBox != null ? _refHeroBox : (_refHeroBox = RefKnight.transform.Find("HeroBox").GetComponent<Collider2D>());

        //used to stop hazard coros
        internal static IEnumerator CurrentHazardCoro;

        internal static IEnumerator CurrentInvulnCoro;


        internal static DebugMod instance;

        public readonly List<Renderer> inventoryRenderers = new();

        public List<GameObject> loadScreens = new();

        private int loadExtender = 1;
        private bool superSlides = false;

        public int LoadExtender
        {
            get => loadExtender;
            set => loadExtender = value;
        }

        public bool SuperSlides
        {
            get => superSlides;
            set => superSlides = value;
        }

        public override float BeforeAdditiveLoad(string scene) => LoadExtender;

        //internal static int NailDamage;

        public static GlobalSettings settings { get; set; } = new GlobalSettings();
        public void OnLoadGlobal(GlobalSettings s)
        {
            DebugMod.settings = s;
            if (settings.binds is null)
            {
                settings.binds = new();
                DebugMod.ResetKeyBinds();
            }
        }
        public GlobalSettings OnSaveGlobal() => DebugMod.settings;
        public SaveSettings LocalSaveData { get; set; } = new SaveSettings();
        public void OnLoadLocal(SaveSettings s) => this.LocalSaveData = s;
        public SaveSettings OnSaveLocal() => this.LocalSaveData;

        private static float _loadTime;
        private static float _unloadTime;
        private static bool _loadingChar;

        internal static bool stateOnDeath;
        internal static bool infiniteHP;
        internal static bool infiniteSoul;
        internal static bool playerInvincible;
        internal static bool noclip;
        internal static Vector3 noclipPos;
        internal static bool cameraFollow;
        internal static SaveStateManager saveStateManager;
        public static bool KeyBindLock;
        internal static bool TimeScaleActive;
        internal static float CurrentTimeScale = 1f;
        internal static bool PauseGameNoUIActive = false;
        internal static bool savestateFixes = true;

        internal static Dictionary<string, (string category, bool allowLock, Action method)> bindMethods = new();
        internal static Dictionary<string, (string category, bool allowLock, Action method)> AdditionalBindMethods = new();

        internal static Dictionary<KeyCode, int> alphaKeyDict = new Dictionary<KeyCode, int>();

        static int alphaStart;
        static int alphaEnd;

        public override List<(string, string)> GetPreloadNames()
        {
            return new List<(string, string)>
            {
                ("Crossroads_01", "_Enemies/Climber 1")
            };
        }

        public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects)
        {
            instance.Log("Initializing");

            TikTikCamController.tiktikClone = preloadedObjects["Crossroads_01"]["_Enemies/Climber 1"];

            float startTime = Time.realtimeSinceStartup;
            instance.Log("Building MethodInfo dict...");
            
            bindMethods.Clear();
            foreach (MethodInfo method in typeof(BindableFunctions).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                object[] attributes = method.GetCustomAttributes(typeof(BindableMethod), false);

                if (attributes.Any())
                {
                    BindableMethod attr = (BindableMethod)attributes[0];
                    string name = attr.name;
                    string cat = attr.category;
                    bool allowLock = attr.allowLock;

                    bindMethods.Add(name, (cat, allowLock, (Action)Delegate.CreateDelegate(typeof(Action), method)));
                }
            }
            
            instance.Log("Done! Time taken: " + (Time.realtimeSinceStartup - startTime) + "s. Found " + bindMethods.Count + " methods");

            if (settings.FirstRun)
            {
                instance.Log("First run detected, setting default binds");

                settings.FirstRun = false;
                ResetKeyBinds();
            }

            if (!Directory.Exists(settings.ModBaseDirectory)) 
            {
                Directory.CreateDirectory(settings.ModBaseDirectory);
            }

            if (settings.NumPadForSaveStates)
            {
                alphaStart = (int)KeyCode.Keypad0;
                alphaEnd = (int)KeyCode.Keypad9;
            }
            else
            {
                alphaStart = (int)KeyCode.Alpha0;
                alphaEnd = (int)KeyCode.Alpha9;
            }

            int alphaInt = 0;
            alphaKeyDict.Clear();
                
            for (int i = alphaStart; i <= alphaEnd; i++)
            {
                KeyCode tmpKeyCode = (KeyCode)i;
                alphaKeyDict.Add(tmpKeyCode, alphaInt++);
            }
            

            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += LevelActivated;
            GameObject UIObj = new GameObject();
            UIObj.AddComponent<GUIController>();
            Object.DontDestroyOnLoad(UIObj);
            
            saveStateManager = new SaveStateManager();
            ModHooks.AfterSavegameLoadHook += LoadCharacter;

            //ModHooks.HitInstanceHook += DoDamage;
            
            ModHooks.NewGameHook += NewCharacter;
            ModHooks.BeforeSceneLoadHook += OnLevelUnload;
            ModHooks.TakeHealthHook += PlayerDamaged;
            ModHooks.ApplicationQuitHook += SaveSettings;

            On.GameManager.PauseGameToggle += PatchSuperslides;

            //hooks needed for savestate fixes
            On.HeroController.HazardRespawn += OnHazardRespawn;
            On.HeroController.Invulnerable += OnInvulnerable;

            if (settings.ShowCursorWhileUnpaused)
            {
                BindableFunctions.SetAlwaysShowCursor();
            }

            BossHandler.PopulateBossLists();
            GUIController.Instance.BuildMenus();
            SceneWatcher.Init();

            KeyBindLock = false;
            TimeScaleActive = false;

            Console.AddLine("New session started " + DateTime.Now);
        }

        public DebugMod()
        {
            instance = this;
            // Register exports early so other mods can use them when initializing
            typeof(DebugExport).ModInterop();

            // idk
            DoTrollMenu();
        }

        #region Troll Menu
        private static int chooser;
        private static bool OpenedSave;
        private void DoTrollMenu()
        {
            chooser = Random.Range(1, 1000);
            OpenedSave = false;
            if (chooser != 1) return;
            GameObject DebugEasterEgg = new GameObject("DebugEasterEgg");
            Object.DontDestroyOnLoad(DebugEasterEgg);

            On.SetVersionNumber.Start += ChangeVersionNumber;
            On.UIManager.SetupRefs += FixMenuTitle;
        }

        private void FixMenuTitle(On.UIManager.orig_SetupRefs orig, UIManager self)
        {
            orig(self);
            if (GameObject.Find("DebugEasterEgg") != null && !OpenedSave && GameManager.instance.sceneName == "Menu_Title")
            {
                Log("Running");

                Texture2D RealTitle_texture = new Texture2D(1, 1);
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("DebugMod.Images.SilkNever.png"))
                {
                    byte[] bytes = new byte[stream.Length];
                    stream.Read(bytes, 0, bytes.Length);
                    RealTitle_texture.LoadImage(bytes, false);
                    RealTitle_texture.name = "SilkNever";
                }

                var RealTitle = Sprite.Create(RealTitle_texture,
                    new Rect(0, 0, RealTitle_texture.width, RealTitle_texture.height),
                    new Vector2(0.5f, 0.5f), self.gameTitle.sprite.pixelsPerUnit, 0, SpriteMeshType.FullRect);

                self.gameTitle.sprite = RealTitle;
            }
        }

        private void ChangeVersionNumber(On.SetVersionNumber.orig_Start orig, SetVersionNumber self)
        {
            orig(self);
            Text textUi = ReflectionHelper.GetField<SetVersionNumber, Text>(self, "textUi");

            if (!(textUi != null)) return;

            string VersionNumber = OpenedSave ? Constants.GAME_VERSION : "1.0.0.5";
            StringBuilder stringBuilder = new StringBuilder(VersionNumber);
            textUi.text = stringBuilder.ToString();
        }

        #endregion


        internal static void ResetKeyBinds()
        {
            settings.binds.Clear();

            settings.binds.Add("Toggle All UI", KeyCode.F1);
            settings.binds.Add("Toggle Info", KeyCode.F2);
            settings.binds.Add("Toggle Menu", KeyCode.F3);
            settings.binds.Add("Toggle Console", KeyCode.F4);
            settings.binds.Add("Full/Min Info Switch", KeyCode.F6);
            settings.binds.Add("Force Camera Follow", KeyCode.F8);
            settings.binds.Add("Toggle Enemy Panel", KeyCode.F9);
            settings.binds.Add("Toggle Binds", KeyCode.BackQuote);
            settings.binds.Add("Nail Damage +4", KeyCode.Equals);
            settings.binds.Add("Nail Damage -4", KeyCode.Minus);
            settings.binds.Add("Increase Timescale", KeyCode.KeypadPlus);
            settings.binds.Add("Decrease Timescale", KeyCode.KeypadMinus);
            settings.binds.Add("Zoom In", KeyCode.PageUp);
            settings.binds.Add("Zoom Out", KeyCode.PageDown);
        }
        private void SaveSettings()
        {
            SaveGlobalSettings();
            instance.Log("Saved");
        }

        private int PlayerDamaged(int damageAmount)
        {

            int damage = infiniteHP ? 0 : damageAmount;
            if (stateOnDeath && (PlayerData.instance.health - damage <= 0))
            {
                saveStateManager.LoadSaveState(SaveStateType.Memory);
                Console.AddLine("Lethal damage prevented, savestate loading");
                return 0;
            }
            return damage;
        }

        //save coros so they can be forcibly stopped
        public static IEnumerator OnHazardRespawn(On.HeroController.orig_HazardRespawn orig, HeroController self)
        {
            CurrentHazardCoro = orig(self);
            return CurrentHazardCoro;
        }

        public static IEnumerator OnInvulnerable(On.HeroController.orig_Invulnerable orig, HeroController self, float duration)
        {
            CurrentInvulnCoro = orig(self, duration);
            return CurrentInvulnCoro;
        }

        private static IEnumerator PatchSuperslides(On.GameManager.orig_PauseGameToggle orig, GameManager self)
        {
            // Check if the player is in a valid state to superslide
            if (!instance.SuperSlides
                || PlayerData.instance.disablePause
                || self.gameState != GameState.PLAYING
                || !ReflectionHelper.GetField<GameManager, bool>(self, "timeSlowed"))
            {
                yield return orig(self);
                yield break;
            }

            // Remove the freezeframe lock on pausing, reset recoil to ensure max speed
            ReflectionHelper.SetFieldSafe(self, "timeSlowed", false);
            ReflectionHelper.SetFieldSafe(HeroController.instance, "recoilStep", 0);

            // Pause the game to cause a superslide
            yield return orig(self);
        }

        private void NewCharacter() => LoadCharacter(null);

        private void LoadCharacter(SaveGameData saveGameData)
        {
            OpenedSave = true;
            var DebugEasterEggChecker = GameObject.Find("DebugEasterEgg");
            if (DebugEasterEggChecker != null) GameObject.Destroy(DebugEasterEggChecker);

            //NailDamage = saveGameData?.playerData.nailDamage ?? 5;
            
            Console.Reset();
            EnemiesPanel.Reset();

            playerInvincible = false;
            infiniteHP = false;
            infiniteSoul = false;
            noclip = false;

            _loadingChar = true;
        }

        private void LevelActivated(Scene sceneFrom, Scene sceneTo)
        {
            string sceneName = sceneTo.name;
            
            if (_loadingChar)
            {
                TimeSpan timeSpan = TimeSpan.FromSeconds(PlayerData.instance.playTime);
                string text = string.Format("{0:00}.{1:00}", Math.Floor(timeSpan.TotalHours), timeSpan.Minutes);
                int profileID = PlayerData.instance.profileID;
                Console.AddLine("New savegame loaded. Profile playtime " + text + " Completion: " + PlayerData.instance.completionPercentage + " Save slot: " + profileID + " Game Version: " + PlayerData.instance.version);
                _loadingChar = false;
            }

            if (GM.IsGameplayScene())
            {
                _loadTime = Time.realtimeSinceStartup;
                Console.AddLine("New scene loaded: " + sceneName);
                EnemiesPanel.Reset();
                PlayerDeathWatcher.Reset();
                BossHandler.LookForBoss(sceneName);
                MethodHelpers.VisualMaskHelper.OnSceneChange(sceneTo);
            }
        }

        private string OnLevelUnload(string toScene)
        {
            _unloadTime = Time.realtimeSinceStartup;

            return toScene;
        }

        public static string GetSceneName()
        {
            if (GM == null)
            {
                instance.LogWarn("GameManager reference is null in GetSceneName");
                return "";
            }

            string sceneName = GM.GetSceneNameString();
            return sceneName;
        }

        public static float GetLoadTime()
        {
            return (float)Math.Round(_loadTime - _unloadTime, 2);
        }

        public static void Teleport(string scenename, Vector3 pos)
        {
            HC.transform.position = pos;

            HC.EnterWithoutInput(false);
            HC.proxyFSM.SendEvent("HeroCtrl-LeavingScene");
            HC.transform.SetParent(null);

            GM.NoLongerFirstGame();
            GM.SaveLevelState();
            GM.SetState(GameState.EXITING_LEVEL);
            GM.entryGateName = "dreamGate";
            RefCamera.FreezeInPlace();

            HC.ResetState();

            GM.LoadScene(scenename);
        }

        /// <summary>
        /// Adds a menu to the top menu, with the provided name and button list.
        /// </summary>
        [PublicAPI]
        public static void AddTopMenuContent(string MenuName, List<TopMenuButton> ButtonList) => TopMenu.AddTopMenuContent(MenuName, ButtonList);
        
        /// <summary>
        /// Add all public static methods on a type to the keybinds list. Methods must be decorated with the BindableMethod attribute.
        /// </summary>
        [PublicAPI]
        public static void AddToKeyBindList(Type BindableFunctionsClass)
        {
            foreach (MethodInfo method in BindableFunctionsClass.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<BindableMethod>(false) is BindableMethod attr)
                {
                    string name = attr.name;
                    string cat = attr.category;
                    bool allowLock = attr.allowLock;

                    instance.Log($"Recieved Action: {name} (from {BindableFunctionsClass.Name})");
                    AdditionalBindMethods.Add(name, (cat, allowLock, (Action)Delegate.CreateDelegate(typeof(Action), method)));
                } 
            }
        }

        /// <summary>
        /// Add an action to the keybinds list.
        /// </summary>
        [PublicAPI]
        public static void AddActionToKeyBindList(Action method, string name, string category)
        {
            AddActionToKeyBindList(method, name, category, true);   
        }

        /// <summary>
        /// Add an action to the keybinds list.
        /// </summary>
        [PublicAPI]
        public static void AddActionToKeyBindList(Action method, string name, string category, bool allowLock)
        {
            instance.Log($"Received Action: {name}");
            AdditionalBindMethods.Add(name, (category, allowLock, method));
        }


        public MenuScreen GetMenuScreen(MenuScreen modListMenu, ModToggleDelegates? toggleDelegates) =>
            ModMenu.CreateMenuScreen(modListMenu).Build();

        public bool ToggleButtonInsideMenu => false;
    }
}
