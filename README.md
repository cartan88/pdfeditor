# PDF Editor

A Windows desktop app for filling in PDF forms and signing them.

**Run it:** `publish\PdfEditor.exe` (needs the .NET 10 Desktop Runtime, which is already on this PC).
You can also drag a PDF onto the exe or the window, or use it with *Open with…*.

## Features
- **Fillable forms (AcroForm):** text fields (including multi-line and comb fields), check boxes, radio buttons, and drop-downs. Fields are highlighted in blue; click one and type.
- **Forms without fields:** the **Text**, **Date**, **✓**, and **✗** tools place text anywhere on the page.
- **Signatures:** click **✍ Sign** to draw a signature or import a scanned/photographed one. The app can turn a white paper background transparent. Saved signatures are stored in `%LOCALAPPDATA%\PdfEditor\Signatures` and reused next time.
  - Drag to move. The corner handle resizes. The round top handle rotates the signature to any angle; hold Shift to snap to 15° steps.
  - **⟲ 90° / ⟳ 90°** (Ctrl+Shift+R / Ctrl+R) rotate the signature for vertical signature lines. You can also type an exact angle.
  - Yellow signature fields: click one and the signature is placed and fitted inside the box. Tall, narrow boxes are rotated automatically.
- **White-out:** drag to cover existing content with a white box. This is a visual cover only, **not redaction**: the text underneath stays in the file and can still be selected, searched and copied.
- **⧉ All pages:** copies the selected item (for example, your initials) onto every page.
- **Image:** places a logo, stamp, or photo. You can also drop an image file onto a page.
- Undo/redo (Ctrl+Z / Ctrl+Y), zoom (Ctrl+mouse wheel, Ctrl+ +/−, Ctrl+0 fits the width), and Del to delete the selected item.
- **Print** (Ctrl+P) prints everything you filled in, exactly as a flattened copy would look. You can choose a page range, the current page, or the number of copies. Oversized pages are shrunk to fit, and landscape pages are rotated to fit the paper.
- **Save** keeps the form fields editable. **File › Save Flattened Copy** turns everything into permanent page content.
- Opens password-protected PDFs and permission-restricted PDFs.

## Build from source
```
cd src\PdfEditor
dotnet build
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ..\..\publish
```
For coworkers who don't have .NET installed, build the standalone version. This is how `PdfEditor-Windows-x64.zip` was made:
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o ..\..\publish-standalone
```

## How it works
- Pages are rendered with the PDF engine built into Windows (`Windows.Data.Pdf`).
- Editing and saving use [PDFsharp](https://www.pdfsharp.com/) (MIT license). Each save starts from the original file plus your changes, so saving more than once never duplicates content.
- Text and signatures are written as real page content, with correct placement on rotated pages and pages with crop boxes. Form values get proper appearance streams, so they show up in Acrobat, Edge, Chrome, and other viewers.

## Limitations
- This app does not edit or delete existing text in the PDF. Use White-out and type over it.
- Signatures are images, not cryptographic digital signatures (certificate-based signing).
- Saving a PDF that already has a digital signature invalidates that signature. The app warns before doing this.
- XFA forms (an old Adobe LiveCycle format) are not supported. Standard AcroForms are.
