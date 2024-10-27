using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAPICodePack.Dialogs;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using System.Collections.Generic;
using DTOGenerator2;


namespace DTOGenerator
{
    [Command(PackageIds.GenerateCommand)]
    internal sealed class GenerateCommand : BaseCommand<GenerateCommand>
    {
        private const string Create = "Create";
        private const string Read = "Read";
        private const string Update = "Update";
        private string[] crudOperations = [Create, Read, Update];
        private string[] ignoreOnCreateWords = { "active", "delete" };

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            // Get the selected folder path from Solution Explorer
            string selectedFolderPath = await GetSelectedFolderPathAsync();

            if (string.IsNullOrEmpty(selectedFolderPath))
            {
                await VS.MessageBox.ShowWarningAsync("DTOGenerator", "Please select a folder.");
                return;
            }

            await CreateDTOClassesAsync(selectedFolderPath);
        }

        private async Task CreateDTOClassesAsync(string entityFolder)
        {
            try
            {

                // Seçilen klasördeki tüm C# dosyalarını bul
                var csFiles = Directory.GetFiles(entityFolder, "*.cs", SearchOption.TopDirectoryOnly);

                using (var dialog = new CommonOpenFileDialog()
                {
                    IsFolderPicker = true,
                    Title = "Select Target Folder for DTOs",
                    // allow new folder adding
                    AllowPropertyEditing = true
                })
                {
                    if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                    {
                        var targetFolder = dialog.FileName;

                        foreach (var csFile in csFiles)
                        {
                            // Sınıfları Roslyn ile analiz et
                            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(csFile), path: csFile);
                            SyntaxNode root = await syntaxTree.GetRootAsync();

                            // Sınıfları bul
                            var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                            foreach (var classDeclaration in classDeclarations)
                                await GenerateDTOSAsync(classDeclaration, entityFolder, targetFolder);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await VS.MessageBox.ShowErrorAsync($"DTO generator failed: {ex.Message}");
            }

        }

        private async Task GenerateDTOSAsync(ClassDeclarationSyntax classDeclaration, string entityFolder, string targetFolder)
        {

            bool shouldIgnore = classDeclaration.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(attr => (attr.Name as IdentifierNameSyntax)?.Identifier.Text == nameof(IgnoreDTOGeneratorAttribute).Replace("Attribute", ""));
            if (shouldIgnore)
                return;

            string targetAssemblyPath = GetAssemblyPathByFolder(targetFolder);
            string targetAssemblyName = Path.GetFileNameWithoutExtension(new FileInfo(targetAssemblyPath).Name);

            if (string.IsNullOrEmpty(targetAssemblyName))
            {
                await VS.MessageBox.ShowErrorAsync($"Ensure {targetFolder} is a valid path and involved in an assembly");
                return;
            }

            string entityDTOFolder = Path.Combine(targetFolder, classDeclaration.Identifier.Text);
            Directory.CreateDirectory(entityDTOFolder);

            // don't do anything if all files already exist
            if (crudOperations.All(operation => File.Exists(Path.Combine(entityDTOFolder, $"{classDeclaration.Identifier.Text}{operation}DTO.cs"))))
                return;

            SyntaxNode node;
            SyntaxTree syntaxTree;
            SourceText sourceText;
            string entityAssemblyPath = GetAssemblyPathByFolder(entityFolder);

            #region Evaluate Syntax to The Class

            IComponentModel componentModel = (IComponentModel)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SComponentModel));
            VisualStudioWorkspace workspace = componentModel.GetService<VisualStudioWorkspace>();

            Microsoft.CodeAnalysis.Project project = workspace.CurrentSolution.Projects.First(p => string.Equals(p.FilePath, entityAssemblyPath, StringComparison.OrdinalIgnoreCase));
            Compilation compilation = await project.GetCompilationAsync();

            SyntaxTree classSyntaxTree = compilation.SyntaxTrees.First(tree => string.Equals(tree.FilePath, classDeclaration.SyntaxTree.FilePath, StringComparison.OrdinalIgnoreCase));
            SemanticModel semanticModel = compilation.GetSemanticModel(classSyntaxTree);

            SyntaxNode root = await classSyntaxTree.GetRootAsync();
            ClassDeclarationSyntax matchedClassDeclaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(cd => cd.Identifier.Text == classDeclaration.Identifier.Text);

            #endregion

            INamedTypeSymbol classSymbol = semanticModel.GetDeclaredSymbol(matchedClassDeclaration);
            List<IPropertySymbol> properties = new();
            var baseType = classSymbol.BaseType;

            // top class properties
            while (baseType != null && !string.Equals(baseType.Name, nameof(Object), StringComparison.OrdinalIgnoreCase))
            {
                // Base sınıfın public ve protected property'lerini al
                var baseProps = baseType.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p => !p.IsReadOnly && !p.IsWriteOnly && p.DeclaredAccessibility == Accessibility.Public || p.DeclaredAccessibility == Accessibility.Protected);
                properties.AddRange(baseProps);
                // Bir üst sınıfa geç
                baseType = baseType.BaseType;
            }

            // class himself properties
            var props = classSymbol.GetMembers().OfType<IPropertySymbol>()
                  .Where(x => !x.IsReadOnly && !x.IsWriteOnly && x.DeclaredAccessibility == Accessibility.Public);
            properties.AddRange(props);

            StringBuilder sb = new StringBuilder();
            string dtoClassName, dtoFilePath;

            foreach (var operation in crudOperations)
            {
                dtoClassName = $"{classSymbol.Name}{operation}DTO";
                dtoFilePath = Path.Combine(entityDTOFolder, $"{dtoClassName}.cs");
                // DO NOT OVERRIDE EXISTING FILE
                if (File.Exists(dtoFilePath))
                    continue;

                sb.Clear();
                sb.AppendLine("using System;");
                sb.AppendLine();
                sb.AppendLine($"namespace {targetAssemblyName}.Domain.DTO");
                sb.AppendLine("{");
                sb.AppendLine($"public class {dtoClassName}");
                sb.AppendLine("{");

                foreach (IPropertySymbol property in properties)
                {

                    if (operation == Create && ignoreOnCreateWords.Any(word => property.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;

                    if (operation == Create && string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (property.Type.TypeKind == TypeKind.Enum)
                    {
                        sb.AppendLine($"    public {property.Type.ToDisplayString()} {property.Name} {{ get; set; }}");
                    }
                    else if (property.Type.SpecialType != SpecialType.None)
                    {
                        sb.AppendLine($"    public {property.Type} {property.Name} {{ get; set; }}");
                    }
                }

                // class
                sb.AppendLine("}");
                // namespace
                sb.AppendLine("}");

                node = await CSharpSyntaxTree.ParseText(sb.ToString()).GetRootAsync();
                syntaxTree = node.NormalizeWhitespace().SyntaxTree;
                sourceText = await syntaxTree.GetTextAsync();
                File.WriteAllText(dtoFilePath, sourceText.ToString());
            }

        }

        private string GetAssemblyPathByFolder(string folder)
        {

            DirectoryInfo currentDirectory = new DirectoryInfo(folder);

            while (currentDirectory != null)
            {
                // O anki klasörde .csproj dosyası var mı kontrol et
                var csprojFiles = currentDirectory.GetFiles("*.csproj");

                if (csprojFiles.Length > 0)
                    return csprojFiles[0].FullName;
                //return Path.GetFileNameWithoutExtension(csprojFiles[0].Name);

                // Bir üst klasöre git
                currentDirectory = currentDirectory.Parent;
            }

            return null;
        }

        private async Task<string> GetSelectedFolderPathAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var monitorSelection = await VS.Services.GetMonitorSelectionAsync();
            IntPtr hierarchyPointer, selectionContainerPointer;
            IVsMultiItemSelect multiItemSelect;
            uint itemid;

            monitorSelection.GetCurrentSelection(out hierarchyPointer, out itemid, out multiItemSelect, out selectionContainerPointer);

            if (itemid == VSConstants.VSITEMID_NIL || hierarchyPointer == IntPtr.Zero)
                return null;

            IVsHierarchy hierarchy = (IVsHierarchy)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(hierarchyPointer);
            ((IVsHierarchy)hierarchy).GetCanonicalName(itemid, out string itemFullPath);

            bool isFolder = Directory.Exists(itemFullPath);

            // Return the path only if it's a folder
            return isFolder ? itemFullPath : null;
        }
    }
}
