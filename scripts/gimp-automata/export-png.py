from gi.repository import Gimp, Gio
import os

input_file = os.environ["GIMP_INPUT"]
output_file = os.environ["GIMP_OUTPUT"]

pdb = Gimp.get_pdb()

load = pdb.lookup_procedure("gimp-file-load")
cfg = load.create_config()
cfg.set_property("run-mode", Gimp.RunMode.NONINTERACTIVE)
cfg.set_property("file", Gio.File.new_for_path(input_file))
img = load.run(cfg).index(1)

save = pdb.lookup_procedure("file-png-export")
cfg = save.create_config()
cfg.set_property("run-mode", Gimp.RunMode.NONINTERACTIVE)
cfg.set_property("image", img)
cfg.set_property("file", Gio.File.new_for_path(output_file))
save.run(cfg)

Gimp.Image.delete(img)

