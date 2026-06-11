import sys
import re

def main():
    filepath = sys.argv[1]
    methods_to_delete = sys.argv[2:]
    
    with open(filepath, 'r', encoding='utf-8') as f:
        lines = f.readlines()
    
    # Pattern: start of method definition with 8-space indent
    method_start_re = re.compile(r'^        private (?:async )?(?:Task|void|bool\?) (\w+)\(')
    
    # Find all method ranges
    methods = []
    for i, line in enumerate(lines):
        m = method_start_re.match(line)
        if m:
            methods.append({'name': m.group(1), 'start': i})
    
    # Determine end of each method (start of next method, or start of nested record/class, or EOF)
    for idx, meth in enumerate(methods):
        end = len(lines)
        for j in range(meth['start'] + 1, len(lines)):
            line = lines[j]
            # Next method at same indent level
            if method_start_re.match(line):
                end = j
                break
            # Nested type definition (record/class/enum) at 8-space indent
            if re.match(r'^        (?:private |public |internal )?(?:sealed |abstract )?(?:record|class|enum|struct|interface) ', line):
                end = j
                break
            # Property getter/setter at 12-space indent — not a method boundary
            # Continue
        meth['end'] = end
    
    # Build set of lines to delete
    lines_to_delete = set()
    for meth in methods:
        if meth['name'] in methods_to_delete:
            for i in range(meth['start'], meth['end']):
                lines_to_delete.add(i)
    
    # Write back non-deleted lines
    new_lines = [line for i, line in enumerate(lines) if i not in lines_to_delete]
    
    # Clean up consecutive blank lines (max 2)
    cleaned = []
    blank_count = 0
    for line in new_lines:
        if line.strip() == '':
            blank_count += 1
            if blank_count <= 2:
                cleaned.append(line)
        else:
            blank_count = 0
            cleaned.append(line)
    
    with open(filepath, 'w', encoding='utf-8') as f:
        f.writelines(cleaned)
    
    print(f"Deleted methods: {methods_to_delete}")
    print(f"Removed {len(lines_to_delete)} lines, file now {len(cleaned)} lines")

if __name__ == '__main__':
    main()
