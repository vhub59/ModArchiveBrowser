using System;
using Dalamud.Interface.Textures.TextureWraps;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using ModArchiveBrowser.Utils;
using Dalamud.Interface.Textures;
using System.Collections.Concurrent;

namespace ModArchiveBrowser.Windows
{
    public class SearchWindow : Window, IDisposable
    {
        private SortBy selectedSortBy = SortBy.Rank;
        private SortOrder selectedSortOrder = SortOrder.Desc;
        private Gender? selectedGender = null;
        private NSFW selectedNSFW = NSFW.False;
        private DTCompatibility selectedDTCompat = DTCompatibility.TexToolsCompatible;
        private HashSet<Types> selectedType = new HashSet<Types>();
        private Plugin plugin;

        private string searchQuery = "";
        private string modName = "";
        private string modRaces = "";
        private string modAuthor = "";
        private string modTags = "";
        private string modAffects = "";
        private string modComments = "";
        private int page = 1;
        private Task searchTask = null;
        private List<ModThumb> modThumbs = new List<ModThumb>();
        ConcurrentDictionary<string, ISharedImmediateTexture> images = new ConcurrentDictionary<string, ISharedImmediateTexture>();
        ConcurrentDictionary<string,Task> imagesTasks = new ConcurrentDictionary<string,Task>();
        public SearchWindow(Plugin plugin)
        : base("XIV Mod Archive Search##modarchivebrowsersearch")
        {
            this.plugin = plugin;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(600, 500),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
        }
        public void Dispose()
        {

        }
        //Search Url should already have been built before being passed
        public void UpdateSearch(string url)
        {
            searchTask = Task.Run((async () =>
                                      {
                                          List<ModThumb> searchRes = WebClient.DoSearch(url);
                                          this.modThumbs=searchRes;
                                          RebuildSharedTextures();
                                      }));
        }

        private void RebuildSharedTextures()
        {
            imagesTasks.Clear();
            foreach (ModThumb modThumb in modThumbs)
            {
                Task thumbnailTask = Task.Run((async () =>
                                                  {
                                                      string path = await plugin.imageHandler.DownloadImage(modThumb.url_thumb);
                                                      ISharedImmediateTexture sharedTexture = Plugin.TextureProvider.GetFromFile(path);
                                                      images.TryAdd(modThumb.url_thumb, sharedTexture);
                                                  }));
                imagesTasks.TryAdd(modThumb.url_thumb, thumbnailTask);
            }
        }

        /// <summary>
        /// Dessine la recherche à l'intérieur de la fenêtre principale.
        ///
        /// Cette classe reste un Window par commodité, mais elle n'est plus jamais ouverte comme
        /// telle : cliquer sur un onglet fermait l'accueil pour ouvrir une seconde fenêtre d'aspect
        /// identique, ce qui donnait l'impression de se perdre. Tout se passe desormais au meme
        /// endroit, et le bouton "Go back to homepage" n'a plus de raison d'etre.
        /// </summary>
        public void DrawEmbedded(string title)
        {
            NavBar.Context(title, modThumbs?.Count ?? 0, page);
            ImGui.Separator();

            DrawSearchHeader();

            if (modThumbs != null && modThumbs.Count > 0 && searchTask is { Status: TaskStatus.RanToCompletion })
            {
                DrawSearchResults();
            }
            else if (searchTask is { IsCompleted: false })
            {
                ImGui.TextDisabled("Searching...");
            }
            else if (modThumbs is { Count: 0 })
            {
                ImGui.TextDisabled("No mod matches these filters.");
            }
        }

        public void DrawSearchHeader()
        {
            // Search Form
            ImGui.InputText("Search for mods...", ref searchQuery, 100);
            ImGui.SameLine();
            if (ImGui.Button("Search"))
            {
                page = 1;
                string url = WebClient.BuildSearchURL(
                    selectedSortBy,
                    selectedSortOrder,
                    basicText: searchQuery,
                    nsfw: selectedNSFW,
                    name: modName,
                    author: modAuthor,
                    gender: selectedGender,
                    race: modRaces,
                    tags: modTags,
                    affects: modAffects,
                    comments: modComments,
                    dtCompatibility: selectedDTCompat,
                    types: selectedType
                );

                Plugin.Logger.Debug(url);
                searchTask = Task.Run((() => {UpdateSearch(url); }));
            }

            // Advanced Search Toggle
            if (ImGui.CollapsingHeader("Advanced Search Options"))
            {
                if (ImGui.BeginChild("leftsearch", new Vector2(200, 0), true))
                {
                    ImGui.InputText("Name", ref modName, 100);
                    ImGui.InputText("Races", ref modRaces, 100);
                    ImGui.InputText("Author", ref modAuthor, 100);
                    ImGui.InputText("Affects", ref modAffects, 100);
                    ImGui.InputText("Tags", ref modTags, 100);
                    ImGui.InputText("Comments", ref modComments, 100);
                    ImGui.EndChild();
                }
                ImGui.SameLine();
                if (ImGui.BeginChild("rightsearch", new Vector2(200, 0), true))
                {
                    // Gender Selection using Enum
                    string[] genderOptions = { "Male", "Female", "Unisex", "Any" };
                    int genderIndex = selectedGender.HasValue ? (int)selectedGender.Value : 3; // 'Any' is the last option
                    if (ImGui.Combo("Gender", ref genderIndex, genderOptions, genderOptions.Length))
                    {
                        if (genderIndex < 3) // Valid gender selected
                            selectedGender = (Gender)genderIndex;
                        else
                            selectedGender = null; // None selected
                    }

                    // NSFW Toggle
                    //La case était désactivée en dur : sans session, XMA répondait 403 sur ces
                    //pages et le filtre n'aurait mené qu'à des erreurs. /anon_login la rend
                    //utilisable, mais seulement si l'utilisateur a donné son accord.
                    bool nsfwAllowed = plugin.Configuration.AllowNsfw;
                    bool nsfwSelected = nsfwAllowed && selectedNSFW == NSFW.True;

                    //"Adult mods only" et non "NSFW" : le filtre de XMA est exclusif, pas additif.
                    //Mesure faite sur le tag bibo+ : sans le parametre, 3391 resultats ; avec
                    //nsfw=true, 1281 — et aucun des 3391 precedents. Cocher ne complete donc pas
                    //la liste, il la remplace entierement, ce que l'ancienne etiquette laissait
                    //croire au point de faire passer le filtre pour casse.
                    ImGui.BeginDisabled(!nsfwAllowed);
                    if (ImGui.Checkbox("Adult mods only", ref nsfwSelected))
                    {
                        selectedNSFW = nsfwSelected ? NSFW.True : NSFW.False;
                    }
                    ImGui.EndDisabled();

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(nsfwAllowed
                            ? "Replaces the results with adult mods only.\nxivmodarchive cannot mix both in one search."
                            : "Enable \"Show adult (NSFW) mods\" in the plugin settings first.");
                    }

                    if (!nsfwAllowed)
                    {
                        //Accord retiré en cours de session : un ancien choix ne doit pas survivre.
                        selectedNSFW = NSFW.False;
                    }
                    // DT Compatibility Dropdown
                    string[] dtCompatOptions = { "Compatible", "Tex Tools partial","Partial Compatibility","Not compatible" };
                    int dtCompatIndex = (int)selectedDTCompat;
                    ImGui.Combo("DT Compatibility", ref dtCompatIndex, dtCompatOptions, dtCompatOptions.Length);
                    selectedDTCompat = (DTCompatibility)dtCompatIndex;

                    // Mod Types using Enum
                    ImGui.Text("Types:");
                    int i = 0;
                    foreach(Types type in Enum.GetValues(typeof(Types)))
                    {
                        if(i%2 == 0)
                        {
                            ImGui.SameLine();
                        }
                        DrawTypeCheckbox(type);
                        i++;
                    }

                    // Sorting Options
                    string[] sortByOptions = { "Relevance", "Release Date", "Name", "Last Version Update", "Views","Views Today", "Downloads","Followers" };
                    int sortByIndex = (int)selectedSortBy;
                    ImGui.Combo("Sort By", ref sortByIndex, sortByOptions, sortByOptions.Length);
                    selectedSortBy = (SortBy)sortByIndex;

                    string[] sortOrderOptions = { "Ascending", "Descending" };
                    int sortOrderIndex = (int)selectedSortOrder;
                    ImGui.Combo("Sort Order", ref sortOrderIndex, sortOrderOptions, sortOrderOptions.Length);
                    selectedSortOrder = (SortOrder)sortOrderIndex;
                    ImGui.EndChild();
                }
            }


        }

        public void DrawTypeCheckbox(Types type)
        {
            bool isSelected = selectedType.Contains(type);
            if (ImGui.Checkbox(type.ToString(),ref isSelected))
            {
                if (isSelected)
                {
                    selectedType.Add(type);
                }
                else
                {
                    selectedType.Remove(type);
                }
            }
        }

        public void DrawSearchResults()
        {
            if (modThumbs == null)
                return;

            //Meme grille que la page d'accueil : colonnes calculees sur la largeur disponible,
            //cartes de hauteur constante, vignettes au bon ratio. Les deux fenetres portaient
            //jusqu'ici le meme code duplique, avec les memes defauts.
            var available = ImGui.GetContentRegionAvail().X;
            var columns = ModGrid.ColumnCount(available);
            var cardWidth = ModGrid.CardWidth(available, columns);

            for (var i = 0; i < modThumbs.Count; i++)
            {
                var thumb = modThumbs[i];

                //TryGetValue et non l'indexeur : une vignette dont le telechargement a echoue
                //n'est jamais ajoutee au dictionnaire et faisait lever une KeyNotFoundException
                //en pleine boucle de rendu.
                IDalamudTextureWrap? texture = null;
                if (images.TryGetValue(thumb.url_thumb, out var shared))
                    texture = shared.GetWrapOrDefault();

                if (ModGrid.Draw($"##searchcard{i}", thumb, texture, cardWidth))
                {
                    try
                    {
                        plugin.modWindow.ChangeMod(thumb);
                        if (!plugin.modWindow.IsOpen)
                        {
                            plugin.modWindow.Toggle();
                        }

                        plugin.modWindow.BringToFront();
                    }
                    catch (Exception e)
                    {
                        Plugin.ReportError("Error while loading mod,check /xllog for details", e);
                    }
                }

                if ((i + 1) % columns != 0 && i < modThumbs.Count - 1)
                {
                    ImGui.SameLine();
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            float windowWidth = ImGui.GetWindowWidth();
            float buttonWidth = 100;

            float centerOffset = (windowWidth - buttonWidth) * 0.5f;

            // Set the cursor position to the calculated offset to center the button
            ImGui.SetCursorPosX(centerOffset);
            
            if (page > 1)
            {
                if (ImGui.ArrowButton("SearchGoBack", ImGuiDir.Left))
                {
                    page = page - 1;
                    string url = WebClient.BuildSearchURL(
                        selectedSortBy,
                        selectedSortOrder,
                        basicText: searchQuery,
                        nsfw: selectedNSFW,
                        name: modName,
                        author: modAuthor,
                        gender: selectedGender,
                        race: modRaces,
                        tags: modTags,
                        affects: modAffects,
                        comments: modComments,
                        dtCompatibility: selectedDTCompat,
                        types: selectedType,
                        page: page
                    );

                    Plugin.Logger.Debug(url);
                    searchTask = Task.Run((() => {UpdateSearch(url); }));
                }
                ImGui.SameLine();
            }
            
            if (ImGui.ArrowButton("SearchGoForward", ImGuiDir.Right))
            {
                page = page + 1;
                string url = WebClient.BuildSearchURL(
                    selectedSortBy,
                    selectedSortOrder,
                    basicText: searchQuery,
                    nsfw: selectedNSFW,
                    name: modName,
                    author: modAuthor,
                    gender: selectedGender,
                    race: modRaces,
                    tags: modTags,
                    affects: modAffects,
                    comments: modComments,
                    dtCompatibility: selectedDTCompat,
                    types: selectedType,
                    page: page
                );

                Plugin.Logger.Debug(url);
                searchTask = Task.Run((() => {UpdateSearch(url); }));
            }
        }
        
        /// <summary>
        /// Jamais appelé : la fenêtre n'est plus ouverte, son contenu est dessiné par la fenêtre
        /// principale via DrawEmbedded. La classe reste un Window pour ne pas defaire le systeme
        /// de fenetres du plugin.
        /// </summary>
        public override void Draw()
        {
        }
    }
}
