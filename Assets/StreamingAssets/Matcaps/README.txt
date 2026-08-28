Matcaps
=======

Every image in this folder (and in any subfolder of it) shows up in the sculpting
app's Material > Matcap palette. Subfolder names become the category headings in
that palette; images dropped straight into this folder land under "Matcaps".

To add your own:

  1. Drop a .png / .jpg / .jpeg / .tga / .bmp file in here, or into a subfolder
     of your own making.
  2. Hit "Rescan Folder" in the Matcap section - no restart needed.

Or use "Import Matcap..." in the same section to pick an image from anywhere on
disk. Imported files are copied in here, so they survive into later sessions.

A matcap should be a square image of a sphere lit exactly how you want the
surface to look. 512x512 is plenty. The sphere should touch all four edges of
the image - the shader maps the surface normal straight onto the image, so any
empty margin around the sphere shows up as a flat ring around the silhouette.

Credits for the bundled sets are in each subfolder's _Credits.txt.
