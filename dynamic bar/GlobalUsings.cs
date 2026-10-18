// Resolve WPF vs WinForms namespace collisions caused by UseWPF + UseWindowsForms
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
