#include <cstdlib>
#include <fstream>
#include <string>

// An offline CLI-contract fixture. Its output is deliberately not a deployable QNN context.
int main(int argc, char** argv)
{
    std::ofstream log(std::getenv("WD_GENERATOR_ARGS"));
    std::string out, name;
    for (int i = 1; i < argc; ++i) {
        log << argv[i] << '\n';
        if (std::string(argv[i]) == "--output_dir") out = argv[i + 1];
        if (std::string(argv[i]) == "--binary_file") name = argv[i + 1];
    }
    if (out.empty() || name.empty()) return 2;
    std::ofstream(out + "/" + name + ".bin") << "test stub, not a deployable context";
    return 0;
}
