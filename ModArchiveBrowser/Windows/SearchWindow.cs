using System;
using Dalamud.Interface.Utility.Raii;
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
        //Total de la recherche et nombre de pages, distincts du contenu de la page courante.
        private int totalCount = 0;
        private int pageCount = 0;
        //Pages XMA necessaires pour remplir la grille, recalcule a chaque frame.
        private int pagesPerView = 1;
        //Prereglage suivi (Trending, Newest, Sponsored), ou null pour une recherche par filtres.
        private string? presetUrl = null;
        //Chargement arme, execute a la premiere frame ou la capacite de la grille est connue.
        private bool pendingReload = false;
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
        /// <summary>
        /// Selectionne un prereglage (Trending, Newest, Sponsored).
        ///
        /// Ne declenche pas la requete tout de suite : le nombre de pages a agreger depend de la
        /// place disponible dans la grille, qui n'est connue qu'au moment du dessin. Le chargement
        /// est donc arme ici et execute a la premiere frame de la vue.
        ///
        /// Auparavant cette methode chargeait elle-meme une page unique, sans passer par
        /// RunSearch : la premiere page d'un onglet arrivait a moitie vide et affichait le nombre
        /// de pages du site au lieu de celui de l'interface. Les fleches, qui passaient par
        /// RunSearch, se comportaient correctement — d'ou une premiere page fautive et une
        /// pagination saine des qu'on la quittait.
        /// </summary>
        public void UpdateSearch(string url)
        {
            presetUrl = url;
            page = 1;
            pendingReload = true;
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
            NavBar.Context(title, totalCount > 0 ? totalCount : modThumbs?.Count ?? 0, modThumbs?.Count ?? 0);
            ImGui.Separator();

            DrawSearchHeader();

            //Les resultats vivent dans leur propre zone defilante, qui occupe toute la hauteur
            //restante. Les filtres gardent ainsi une place bornee en haut et les resultats sont
            //toujours visibles : il fallait auparavant replier les options pour les apercevoir.
            ImGui.Separator();

            //La capacite de la grille est mesuree ici, a l'endroit ou l'on connait la place
            //reellement disponible : c'est elle qui decide combien de pages XMA une page
            //d'affichage agrege.
            pagesPerView = Math.Clamp(
                (int)Math.Ceiling(ModGrid.Capacity(ImGui.GetContentRegionAvail()) / 15.0), 1, 4);

            if (pendingReload)
            {
                pendingReload = false;
                RunSearch(1);
            }

            if (ImGui.BeginChild("searchresults", new Vector2(0, 0), false))
            {
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
            ImGui.EndChild();
        }

        public void DrawSearchHeader()
        {
            //Le libelle du champ ("Search for mods...") s'affichait a sa droite, colle au bouton
            //Search : on ne savait plus lequel des deux appartenait a quoi. Il devient un texte
            //d'invite a l'interieur du champ, et la touche Entree lance la recherche.
            var buttonWidth = ImGui.CalcTextSize("Search").X + ImGui.GetStyle().FramePadding.X * 2f;
            ImGui.SetNextItemWidth(-(buttonWidth + ImGui.GetStyle().ItemSpacing.X));

            var submitted = ImGui.InputTextWithHint(
                "##searchquery",
                "Search for mods...",
                ref searchQuery,
                100,
                ImGuiInputTextFlags.EnterReturnsTrue);

            ImGui.SameLine();
            if (ImGui.Button("Search") || submitted)
            {
                //On quitte le prereglage : ce sont les filtres qui commandent desormais.
                presetUrl = null;
                RunSearch(1);
            }

            // Advanced Search Toggle
            if (ImGui.CollapsingHeader("Advanced Search Options"))
            {
                DrawAdvancedOptions();
            }


        }

        /// <summary>
        /// Charge une page d'affichage.
        ///
        /// XMA sert quinze mods par requete et n'accepte aucun parametre pour en servir
        /// davantage : per_page, limit, pageSize, count et results ont tous ete essayes, tous
        /// renvoient quinze. Une page large en affiche pourtant une trentaine sans defilement.
        /// On agrege donc autant de pages du site qu'il en faut pour remplir la grille, et la
        /// pagination avance par blocs de cette taille.
        ///
        /// Le nombre de requetes est plafonne a quatre : au-dela, un simple clic sur la fleche
        /// declencherait une rafale vers XMA pour un gain d'affichage negligeable.
        /// </summary>
        private void RunSearch(int targetPage)
        {
            page = Math.Max(1, targetPage);

            var batch = Math.Clamp(pagesPerView, 1, 4);
            var firstSitePage = (page - 1) * batch + 1;

            searchTask = Task.Run(() =>
            {
                var collected = new List<ModThumb>();
                var seen = new HashSet<string>();
                var total = 0;
                var sitePages = 0;

                for (var offset = 0; offset < batch; offset++)
                {
                    var res = WebClient.DoSearch(UrlForSitePage(firstSitePage + offset));
                    total = res.TotalCount;
                    sitePages = res.PageCount;

                    //Un meme mod peut revenir d'une requete a l'autre si son classement bouge
                    //entre les deux, le tri du jour evoluant en continu.
                    foreach (var mod in res.Mods)
                    {
                        if (seen.Add(mod.url))
                            collected.Add(mod);
                    }

                    if (sitePages > 0 && firstSitePage + offset >= sitePages)
                        break;
                }

                this.modThumbs = collected;
                this.totalCount = total;
                this.pageCount = sitePages > 0 ? (int)Math.Ceiling(sitePages / (double)batch) : 0;
                RebuildSharedTextures();
            });
        }

        /// <summary>
        /// URL d'une page du site : celle du prereglage si l'on en suit un, sinon celle
        /// construite a partir des filtres.
        /// </summary>
        private string UrlForSitePage(int sitePage)
            => presetUrl != null ? $"{presetUrl}&page={sitePage}" : BuildUrl(sitePage);

        private string BuildUrl(int sitePage) => WebClient.BuildSearchURL(
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
            page: sitePage);

        /// <summary>Largeur réservée aux libellés, pour que les champs s'alignent entre eux.</summary>
        private const float LabelWidth = 80f;

        /// <summary>
        /// Champ texte précédé de son libellé.
        ///
        /// ImGui place ses libellés à droite du champ par défaut : les panneaux etant figes a
        /// 200 pixels, "Comments" s'affichait "Commer", "DT Compatibility" devenait "DT Comp" et
        /// "Sort Order" finissait en "Sort Orde". Le libelle passe a gauche, sur une largeur
        /// constante, et le champ occupe tout le reste.
        /// </summary>
        private static void LabeledInput(string label, ref string value)
        {
            ImGui.TextUnformatted(label);
            ImGui.SameLine(LabelWidth);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText($"##{label}", ref value, 100);
        }

        private static bool LabeledCombo(string label, ref int index, string[] options)
        {
            ImGui.TextUnformatted(label);
            ImGui.SameLine(LabelWidth);
            ImGui.SetNextItemWidth(-1);
            return ImGui.Combo($"##{label}", ref index, options, options.Length);
        }

        /// <summary>
        /// Options avancées : deux panneaux de même largeur, puis les types sur toute la largeur.
        ///
        /// Les panneaux faisaient auparavant 200 pixels chacun, quelle que soit la taille de la
        /// fenetre : les libelles etaient tronques et les trois quarts de la place restaient
        /// vides. Les types, ranges deux par deux, occupaient a eux seuls la hauteur d'un ecran
        /// et repoussaient les resultats hors de vue.
        /// </summary>
        private void DrawAdvancedOptions()
        {
            var style = ImGui.GetStyle();
            var available = ImGui.GetContentRegionAvail().X;
            var panelWidth = (available - style.ItemSpacing.X) / 2f;
            var panelHeight = ImGui.GetFrameHeightWithSpacing() * 6f + style.WindowPadding.Y * 2f;

            if (ImGui.BeginChild("searchfilters", new Vector2(panelWidth, panelHeight), true))
            {
                LabeledInput("Name", ref modName);
                LabeledInput("Author", ref modAuthor);
                LabeledInput("Races", ref modRaces);
                LabeledInput("Tags", ref modTags);
                LabeledInput("Affects", ref modAffects);
                LabeledInput("Comments", ref modComments);
            }
            ImGui.EndChild();

            ImGui.SameLine();

            if (ImGui.BeginChild("searchoptions", new Vector2(panelWidth, panelHeight), true))
            {
                string[] genderOptions = { "Male", "Female", "Unisex", "Any" };
                var genderIndex = selectedGender.HasValue ? (int)selectedGender.Value : 3;
                if (LabeledCombo("Gender", ref genderIndex, genderOptions))
                    selectedGender = genderIndex < 3 ? (Gender)genderIndex : null;

                string[] dtCompatOptions = { "Compatible", "Tex Tools partial", "Partial Compatibility", "Not compatible" };
                var dtCompatIndex = (int)selectedDTCompat;
                LabeledCombo("DT compat", ref dtCompatIndex, dtCompatOptions);
                selectedDTCompat = (DTCompatibility)dtCompatIndex;

                string[] sortByOptions = { "Relevance", "Release Date", "Name", "Last Version Update", "Views", "Views Today", "Downloads", "Followers" };
                var sortByIndex = (int)selectedSortBy;
                LabeledCombo("Sort by", ref sortByIndex, sortByOptions);
                selectedSortBy = (SortBy)sortByIndex;

                string[] sortOrderOptions = { "Ascending", "Descending" };
                var sortOrderIndex = (int)selectedSortOrder;
                LabeledCombo("Order", ref sortOrderIndex, sortOrderOptions);
                selectedSortOrder = (SortOrder)sortOrderIndex;

                ImGui.Spacing();
                DrawAdultToggle();
            }
            ImGui.EndChild();

            //Les types occupent toute la largeur, repartis sur autant de colonnes qu'il en tient.
            //Sa hauteur est calculee sur le nombre de rangees reellement necessaires : passer
            //Vector2(0, 0) signifie "prends tout l'espace restant" et le panneau descendait
            //jusqu'en bas de la fenetre, repoussant les resultats hors de l'ecran.
            const float columnWidth = 150f;
            var typeCount = Enum.GetValues(typeof(Types)).Length;
            var columns = Math.Max(1, (int)((available - style.WindowPadding.X * 2f) / columnWidth));
            var rows = (int)Math.Ceiling(typeCount / (double)columns);
            var typesHeight = ImGui.GetTextLineHeightWithSpacing()
                            + rows * ImGui.GetFrameHeightWithSpacing()
                            + style.WindowPadding.Y * 2f;

            if (ImGui.BeginChild("searchtypes", new Vector2(0, typesHeight), true))
            {
                ImGui.TextDisabled("Types");

                var index = 0;
                foreach (Types type in Enum.GetValues(typeof(Types)))
                {
                    if (index % columns != 0)
                        ImGui.SameLine(columnWidth * (index % columns));

                    DrawTypeCheckbox(type);
                    index++;
                }
            }
            ImGui.EndChild();
        }

        private void DrawAdultToggle()
        {
            //La case était désactivée en dur : sans session, XMA répondait 403 sur ces pages et le
            //filtre n'aurait mené qu'à des erreurs. /anon_login la rend utilisable, mais seulement
            //si l'utilisateur a donné son accord dans la configuration.
            var nsfwAllowed = plugin.Configuration.AllowNsfw;
            var nsfwSelected = nsfwAllowed && selectedNSFW == NSFW.True;

            //"Adult mods only" et non "NSFW" : le filtre de XMA est exclusif, pas additif. Mesure
            //faite sur le tag bibo+ : 3391 resultats sans le parametre, 1281 avec nsfw=true, sans
            //recouvrement. Cocher ne complete pas la liste, il la remplace.
            ImGui.BeginDisabled(!nsfwAllowed);
            if (ImGui.Checkbox("Adult mods only", ref nsfwSelected))
                selectedNSFW = nsfwSelected ? NSFW.True : NSFW.False;
            ImGui.EndDisabled();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(nsfwAllowed
                    ? "Replaces the results with adult mods only.\nxivmodarchive cannot mix both in one search."
                    : "Enable \"Show adult (NSFW) mods\" in the plugin settings first.");
            }

            //Accord retiré en cours de session : un ancien choix ne doit pas survivre.
            if (!nsfwAllowed)
                selectedNSFW = NSFW.False;
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

                var availability = AvailabilityIndex.Get(plugin.Configuration, thumb.url);

                if (ModGrid.Draw($"##searchcard{i}", thumb, texture, cardWidth, availability))
                {
                    try
                    {
                        plugin.modWindow.ChangeMod(thumb);
                        plugin.MainWindow.ShowingMod = true;
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
            //Pagination par blocs : une page d'affichage vaut plusieurs pages du site.
            ImGui.Spacing();
            ImGui.Separator();

            var loading = searchTask is { IsCompleted: false };
            var lastPage = pageCount > 0 ? pageCount : page;
            var pageLabel = loading ? "Loading..." : $"page {page} of {lastPage:N0}";

            var arrow = ImGui.GetFrameHeight();
            var totalWidth = arrow * 2f + ImGui.CalcTextSize(pageLabel).X + ImGui.GetStyle().ItemSpacing.X * 4f;
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - totalWidth) * 0.5f);

            using (ImRaii.Disabled(loading || page <= 1))
            {
                if (ImGui.ArrowButton("SearchGoBack", ImGuiDir.Left))
                    RunSearch(page - 1);
            }

            ImGui.SameLine();
            ImGui.TextDisabled(pageLabel);
            ImGui.SameLine();

            using (ImRaii.Disabled(loading || (pageCount > 0 && page >= pageCount)))
            {
                if (ImGui.ArrowButton("SearchGoForward", ImGuiDir.Right))
                    RunSearch(page + 1);
            }

            ImGui.Spacing();
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
